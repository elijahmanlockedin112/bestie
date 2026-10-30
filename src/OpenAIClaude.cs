// OpenAIClaude.cs - Claude models (claude-*) answer Bestie's chat-completions bodies through Anthropic's Messages
// API: the body is translated into a Messages request and the answer back into the chat shape
// ({choices:[{message:{content, tool_calls}}]}), streaming included, so every caller works unchanged. Raw HTTP because
// Bestie builds with the C# compiler that ships with Windows (.NET Framework, no NuGet), which the official C# SDK
// doesn't run on. Every call goes through ClaudeLedger (Claude.cs) first. C# 5 only.
//
// Two private body keys ride along and never reach OpenAI (WithoutPrivate strips them there):
//   "_feature"  what the call is for, filed in the spend ledger ("chat", "visual", "memory"...)
//   "_claude"   on an assistant message: Claude's own content blocks (its thinking included), sent back unchanged
//               while the turn's tool loop is still going
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Bestie
{
    public sealed partial class OpenAIClient
    {
        const string ClaudeUrl = "https://api.anthropic.com/v1/messages";
        const string ClaudeVersion = "2023-06-01";

        public static bool IsClaude(string model)
        {
            return (model ?? "").StartsWith("claude-", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A copy of the body without Bestie's private keys (for OpenAI, which would refuse them).</summary>
        static Dictionary<string, object> WithoutPrivate(Dictionary<string, object> body)
        {
            bool any = body.ContainsKey("_feature");
            foreach (var m in Messages(body)) if (m.ContainsKey("_claude")) { any = true; break; }
            if (!any) return body;
            var b = new Dictionary<string, object>(body);
            b.Remove("_feature");
            object v;
            if (b.TryGetValue("messages", out v) && v is IEnumerable)
            {
                var list = new List<object>();
                foreach (object o in (IEnumerable)v)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("_claude")) { d = new Dictionary<string, object>(d); d.Remove("_claude"); }
                    list.Add(d ?? o);
                }
                b["messages"] = list;
            }
            return b;
        }

        // ------------------------------------------------------------------ chat -> messages

        /// <summary>A chat-completions body, answered by Claude. Returns the chat-completions shape.</summary>
        Dictionary<string, object> ViaClaude(Dictionary<string, object> chat, int timeoutMs, bool stream, Action<string> onContent, Action onToolCall)
        {
            string model = Json.Str(chat, "model") ?? "";
            string feature = Json.Str(chat, "_feature") ?? "assistant";
            string key = ClaudeKey.Get();
            if (key.Length == 0)
            {
                var e = new OpenAIException("i don't have a claude key. it lives in synaptic's .env as ANTHROPIC_API_KEY.", "claude: no key");
                e.Status = 401;
                throw e;
            }
            string effort;
            Dictionary<string, object> body = ToClaude(chat, out effort);
            if (stream) body["stream"] = true;
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(body));
            ClaudeLedger.BeforeCall(model, (int)Json.Num(body, "max_tokens", 4096), bytes.Length);

            var acc = new ClaudeAcc();
            HttpWebRequest req = null;
            try
            {
                if (aborted) throw AbortedException();
                Uri uri = new Uri(ClaudeUrl);
                req = (HttpWebRequest)WebRequest.Create(uri);
                req.Method = "POST";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "Bestie/1.0";
                req.ContentType = "application/json";
                req.Accept = stream ? "text/event-stream" : "application/json";
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.Headers["x-api-key"] = key;
                req.Headers["anthropic-version"] = ClaudeVersion;
                lock (gate)
                {
                    if (aborted) throw AbortedException();
                    current = req;
                }
                req.ContentLength = bytes.Length;
                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    acc.RequestId = resp.Headers["request-id"];
                    if (stream && (resp.ContentType ?? "").IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) >= 0)
                        ReadClaudeStream(resp, acc, model, onContent, onToolCall);
                    else
                    {
                        string text = ReadAll(resp);
                        Dictionary<string, object> whole = null;
                        try { whole = Json.Obj(text); } catch { }
                        if (whole == null) throw new OpenAIException("the big outside said something i couldn't read.", "claude: unparseable response: " + Trunc(text, 200));
                        acc.TakeWhole(whole);
                    }
                }
            }
            catch (WebException ex)
            {
                OpenAIException mapped = MapClaude(ex, model);
                if (acc.RequestId == null && ex.Response != null) { try { acc.RequestId = ex.Response.Headers["request-id"]; } catch { } }
                RecordClaude(feature, model, effort, acc, mapped.Aborted ? "aborted" : mapped.Message);
                throw mapped;
            }
            catch (IOException ex)
            {
                RecordClaude(feature, model, effort, acc, aborted ? "aborted" : "network: " + ex.Message);
                if (aborted) throw AbortedException();
                var e = new OpenAIException(NetworkDown, ex.Message);
                e.Network = true;
                throw e;
            }
            catch (OpenAIException ex)
            {
                RecordClaude(feature, model, effort, acc, ex.Aborted ? "aborted" : ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                // anything else after the request went out (a stream torn down mid-read...): still billed, still recorded
                RecordClaude(feature, model, effort, acc, aborted ? "aborted" : ex.GetType().Name + ": " + ex.Message);
                if (aborted) throw AbortedException();
                throw new OpenAIException("the big outside said something i couldn't read.", "claude: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                lock (gate) { if (current == req) current = null; }
            }
            string problem = acc.StopReason == "refusal" ? "refusal" : stream && !acc.Stopped && acc.Whole == null ? "stream ended early" : null;
            RecordClaude(feature, model, effort, acc, problem);
            Dictionary<string, object> r = FromClaude(acc);
            if (!stream) FeedWhole(r, onContent, onToolCall);
            return r;
        }

        static void RecordClaude(string feature, string model, string effort, ClaudeAcc a, string error)
        {
            // (priced by the answering model's name when the table knows it, else by the one asked for, e.g. a dated alias)
            string used = a.Model != null && ClaudeLedger.Known(a.Model) ? a.Model : model;
            ClaudeLedger.Record(feature, used, effort, error == null, error, a.RequestId, a.Input, a.Output, a.CacheWrite, a.CacheRead);
        }

        /// <summary>The Messages request for a chat body. effort: what the ledger files it under.</summary>
        static Dictionary<string, object> ToClaude(Dictionary<string, object> chat, out string effort)
        {
            string model = Json.Str(chat, "model") ?? "";
            var b = new Dictionary<string, object>();
            b["model"] = model;

            // thinking: always adaptive. Bestie's "none"/"minimal" (and no setting) is low effort, where claude mostly skips
            // thinking and answers as fast as with it off. Measured on haiku 5.5 (Oct 30): with thinking off it wrote its
            // reasoning as text before tool calls ("this is a how-does-it-work question, so...") and bestie read that aloud;
            // adaptive at low effort didn't, at the same 1-2 s.
            string want = (Json.Str(chat, "reasoning_effort") ?? "").Trim().ToLowerInvariant();
            bool think = want == "low" || want == "medium" || want == "high" || want == "xhigh" || want == "max";
            effort = think ? want : "low";
            int max = (int)Json.Num(chat, "max_completion_tokens", Json.Num(chat, "max_tokens", 4096));
            if (max <= 0) max = 4096;
            b["thinking"] = new Dictionary<string, object> { { "type", "adaptive" } };
            max += effort == "low" ? 3000 : effort == "medium" ? 8000 : 16000;   // thinking counts against max_tokens
            b["output_config"] = new Dictionary<string, object> { { "effort", effort } };
            b["max_tokens"] = max;

            // the chat's own messages, as a list (the turn's start is where Claude's own blocks may be sent back from)
            var msgs = new List<Dictionary<string, object>>(Each(chat, "messages"));
            int turnStart = -1;
            for (int i = 0; i < msgs.Count; i++)
            {
                if ((Json.Str(msgs[i], "role") ?? "") != "user") continue;
                string prev = i > 0 ? Json.Str(msgs[i - 1], "role") ?? "" : "";
                if (prev != "tool") turnStart = i;
            }

            var system = new List<object>();
            var outMsgs = new List<Dictionary<string, object>>();
            var pendingContext = new List<string>();
            for (int i = 0; i < msgs.Count; i++)
            {
                Dictionary<string, object> m = msgs[i];
                string role = Json.Str(m, "role") ?? "user";
                object content = m.ContainsKey("content") ? m["content"] : null;
                if (role == "system" || role == "developer")
                {
                    string t = Text(content);
                    if (t.Length == 0) continue;
                    // the first one is the cached instructions; later ones (live context) ride in front of the next user message
                    if (outMsgs.Count == 0 && system.Count == 0) system.Add(TextBlock(t));
                    else pendingContext.Add(t);
                    continue;
                }
                var blocks = new List<object>();
                string outRole;
                if (role == "tool")
                {
                    outRole = "user";
                    var tr = new Dictionary<string, object>();
                    tr["type"] = "tool_result";
                    tr["tool_use_id"] = ClaudeId(Json.Str(m, "tool_call_id"));
                    string t = Text(content);
                    tr["content"] = t.Length > 0 ? t : "(done)";
                    blocks.Add(tr);
                }
                else if (role == "assistant")
                {
                    outRole = "assistant";
                    object raw;
                    if (i > turnStart && m.TryGetValue("_claude", out raw) && raw != null)
                    {
                        foreach (object o in AsList(raw)) blocks.Add(o);
                    }
                    else
                    {
                        string said = Text(content);
                        if (said.Trim().Length > 0) blocks.Add(TextBlock(said));
                        foreach (Dictionary<string, object> tc in Each(m, "tool_calls"))
                        {
                            Dictionary<string, object> fn = Json.Obj(tc, "function");
                            var tu = new Dictionary<string, object>();
                            tu["type"] = "tool_use";
                            tu["id"] = ClaudeId(Json.Str(tc, "id"));
                            tu["name"] = Json.Str(fn, "name") ?? "";
                            Dictionary<string, object> input = null;
                            try { input = Json.Obj(Json.Str(fn, "arguments") ?? "{}"); } catch { }
                            tu["input"] = input ?? new Dictionary<string, object>();
                            blocks.Add(tu);
                        }
                    }
                    if (blocks.Count == 0) continue;
                }
                else
                {
                    outRole = "user";
                    if (pendingContext.Count > 0)
                    {
                        foreach (string c in pendingContext) blocks.Add(TextBlock(c));
                        pendingContext.Clear();
                    }
                    if (content is string) { if (((string)content).Trim().Length > 0) blocks.Add(TextBlock((string)content)); }
                    else
                    {
                        foreach (object po in AsList(content))
                        {
                            var p = po as Dictionary<string, object>;
                            if (p == null) continue;
                            string type = Json.Str(p, "type");
                            if (type == "text") { string t = Json.Str(p, "text") ?? ""; if (t.Trim().Length > 0) blocks.Add(TextBlock(t)); }
                            else if (type == "image_url")
                            {
                                Dictionary<string, object> img = ImageBlock(Json.Str(Json.Obj(p, "image_url"), "url"));
                                if (img != null) blocks.Add(img);
                            }
                        }
                    }
                    if (blocks.Count == 0) blocks.Add(TextBlock("…"));
                }
                // consecutive turns of one role become one message (tool results first, as Claude wants them)
                Dictionary<string, object> last = outMsgs.Count > 0 ? outMsgs[outMsgs.Count - 1] : null;
                if (last != null && (string)last["role"] == outRole)
                {
                    var into = (List<object>)last["content"];
                    if (outRole == "user" && role == "tool")
                    {
                        int at = 0;
                        while (at < into.Count && Json.Str(into[at] as Dictionary<string, object>, "type") == "tool_result") at++;
                        into.InsertRange(at, blocks);
                    }
                    else into.AddRange(blocks);
                }
                else
                {
                    var om = new Dictionary<string, object>();
                    om["role"] = outRole;
                    om["content"] = blocks;
                    outMsgs.Add(om);
                }
            }
            if (pendingContext.Count > 0)
            {
                var ctxBlocks = new List<object>();
                foreach (string c in pendingContext) ctxBlocks.Add(TextBlock(c));
                Dictionary<string, object> last = outMsgs.Count > 0 ? outMsgs[outMsgs.Count - 1] : null;
                if (last != null && (string)last["role"] == "user") ((List<object>)last["content"]).AddRange(ctxBlocks);
                else outMsgs.Add(new Dictionary<string, object> { { "role", "user" }, { "content", ctxBlocks } });
            }
            if (outMsgs.Count == 0 || (string)outMsgs[0]["role"] != "user")
                outMsgs.Insert(0, new Dictionary<string, object> { { "role", "user" }, { "content", new List<object> { TextBlock("(start)") } } });
            // claude sometimes says what it's about to do before a tool call, and bestie would read that aloud
            if (chat.ContainsKey("tools"))
                system.Add(TextBlock("When a tool is needed, call it straight away with no text before it. Everything you write is shown and read aloud to them, so never narrate what you're about to do or why."));
            if (system.Count > 0)
            {
                ((Dictionary<string, object>)system[system.Count - 1])["cache_control"] = new Dictionary<string, object> { { "type", "ephemeral" } };
                b["system"] = system;
            }
            b["messages"] = outMsgs;
            b["cache_control"] = new Dictionary<string, object> { { "type", "ephemeral" } };   // the conversation so far, cached for the next message

            var tools = new List<object>();
            foreach (Dictionary<string, object> t in Each(chat, "tools"))
            {
                Dictionary<string, object> fn = Json.Obj(t, "function");
                if (fn == null) continue;
                var ct = new Dictionary<string, object>();
                ct["name"] = Json.Str(fn, "name") ?? "";
                ct["description"] = Json.Str(fn, "description") ?? "";
                object ps;
                ct["input_schema"] = fn.TryGetValue("parameters", out ps) && ps != null ? ps : new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() } };
                tools.Add(ct);
            }
            if (tools.Count > 0)
            {
                b["tools"] = tools;
                object choice;
                if (chat.TryGetValue("tool_choice", out choice) && choice != null)
                {
                    var forced = choice as Dictionary<string, object>;
                    string name = forced != null ? Json.Str(Json.Obj(forced, "function"), "name") : null;
                    // a forced tool is a 400 on opus 5.5 and sonnet 5.5 (auto there; the prompt already asks for it)
                    bool canForce = model.ToLowerInvariant().Contains("haiku");
                    if (!string.IsNullOrEmpty(name) && canForce) b["tool_choice"] = new Dictionary<string, object> { { "type", "tool" }, { "name", name } };
                    else if ((choice as string) == "none") b["tool_choice"] = new Dictionary<string, object> { { "type", "none" } };
                    else if ((choice as string) == "required" && canForce) b["tool_choice"] = new Dictionary<string, object> { { "type", "any" } };
                    else b["tool_choice"] = new Dictionary<string, object> { { "type", "auto" } };
                }
            }
            return b;
        }

        static Dictionary<string, object> TextBlock(string text)
        {
            return new Dictionary<string, object> { { "type", "text" }, { "text", text } };
        }

        /// <summary>A data: url or a web url as a Claude image block (null if it's neither).</summary>
        static Dictionary<string, object> ImageBlock(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            var src = new Dictionary<string, object>();
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int semi = url.IndexOf(';'), comma = url.IndexOf(',');
                if (semi < 0 || comma < semi) return null;
                string media = url.Substring(5, semi - 5).ToLowerInvariant();
                if (media == "image/jpg") media = "image/jpeg";
                if (media != "image/jpeg" && media != "image/png" && media != "image/gif" && media != "image/webp") return null;
                src["type"] = "base64";
                src["media_type"] = media;
                src["data"] = url.Substring(comma + 1);
            }
            else if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { src["type"] = "url"; src["url"] = url; }
            else return null;
            return new Dictionary<string, object> { { "type", "image" }, { "source", src } };
        }

        /// <summary>Claude's tool ids are [a-zA-Z0-9_-]; an OpenAI-made id is cleaned to fit.</summary>
        static string ClaudeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "call_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var sb = new StringBuilder(id.Length);
            foreach (char c in id) sb.Append(char.IsLetterOrDigit(c) && c < 128 || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ messages -> chat

        sealed class ClaudeBlock
        {
            public string Type;
            public Dictionary<string, object> Start;
            public readonly StringBuilder Text = new StringBuilder();
            public readonly StringBuilder Args = new StringBuilder();
            public readonly StringBuilder Thinking = new StringBuilder();
            public string Signature;
        }

        sealed class ClaudeAcc
        {
            public string Model, RequestId, StopReason;
            public bool Stopped;   // message_stop arrived (the answer is whole)
            public long Input, Output, CacheWrite, CacheRead;
            public readonly SortedDictionary<int, ClaudeBlock> Blocks = new SortedDictionary<int, ClaudeBlock>();
            public List<object> Whole;   // a non-streamed answer's content as it came

            public void Usage(Dictionary<string, object> u)
            {
                if (u == null) return;
                Input = Math.Max(Input, (long)Bestie.Json.Num(u, "input_tokens", 0));
                Output = Math.Max(Output, (long)Bestie.Json.Num(u, "output_tokens", 0));
                CacheWrite = Math.Max(CacheWrite, (long)Bestie.Json.Num(u, "cache_creation_input_tokens", 0));
                CacheRead = Math.Max(CacheRead, (long)Bestie.Json.Num(u, "cache_read_input_tokens", 0));
            }

            public void TakeWhole(Dictionary<string, object> msg)
            {
                Model = Bestie.Json.Str(msg, "model") ?? Model;
                StopReason = Bestie.Json.Str(msg, "stop_reason");
                Usage(Bestie.Json.Obj(msg, "usage"));
                Whole = new List<object>(Bestie.Json.Arr(msg, "content"));
            }

            /// <summary>The answer's content blocks, as Claude wants them back.</summary>
            public List<object> Content()
            {
                if (Whole != null) return Whole;
                var list = new List<object>();
                foreach (ClaudeBlock b in Blocks.Values)
                {
                    var d = new Dictionary<string, object>();
                    d["type"] = b.Type;
                    if (b.Type == "text") { if (b.Text.Length == 0) continue; d["text"] = b.Text.ToString(); }
                    else if (b.Type == "tool_use")
                    {
                        d["id"] = Bestie.Json.Str(b.Start, "id") ?? "";
                        d["name"] = Bestie.Json.Str(b.Start, "name") ?? "";
                        object input = null;
                        string js = b.Args.ToString().Trim();
                        if (js.Length > 0) { try { input = Bestie.Json.Parse(js); } catch { DebugLog.Write("claude: a tool call's arguments didn't parse: " + Trunc(js, 160)); } }
                        if (input == null && js.Length > 0 && StopReason == "max_tokens") continue;   // cut off mid-call: never run it with no arguments
                        d["input"] = input as Dictionary<string, object> ?? new Dictionary<string, object>();
                    }
                    else if (b.Type == "thinking") { d["thinking"] = b.Thinking.ToString(); d["signature"] = b.Signature ?? ""; }
                    else if (b.Type == "redacted_thinking") d["data"] = Bestie.Json.Str(b.Start, "data") ?? "";
                    else continue;
                    list.Add(d);
                }
                return list;
            }
        }

        void ReadClaudeStream(HttpWebResponse resp, ClaudeAcc acc, string model, Action<string> onContent, Action onToolCall)
        {
            bool toldToolCall = false;
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:")) continue;
                    string data = line.Substring(5).Trim();
                    if (data.Length == 0) continue;
                    Dictionary<string, object> ev = null;
                    try { ev = Json.Obj(data); } catch { }
                    if (ev == null) continue;
                    string type = Json.Str(ev, "type") ?? "";
                    if (type == "message_start")
                    {
                        Dictionary<string, object> msg = Json.Obj(ev, "message");
                        acc.Model = Json.Str(msg, "model") ?? acc.Model;
                        acc.Usage(Json.Obj(msg, "usage"));
                    }
                    else if (type == "content_block_start")
                    {
                        int idx = (int)Json.Num(ev, "index", acc.Blocks.Count);
                        Dictionary<string, object> cb = Json.Obj(ev, "content_block");
                        var b = new ClaudeBlock { Type = Json.Str(cb, "type") ?? "text", Start = cb };
                        string t = Json.Str(cb, "text");
                        if (!string.IsNullOrEmpty(t)) b.Text.Append(t);
                        acc.Blocks[idx] = b;
                        if (b.Type == "tool_use" && !toldToolCall && onToolCall != null) { toldToolCall = true; try { onToolCall(); } catch { } }
                    }
                    else if (type == "content_block_delta")
                    {
                        int idx = (int)Json.Num(ev, "index", 0);
                        ClaudeBlock b;
                        if (!acc.Blocks.TryGetValue(idx, out b)) { b = new ClaudeBlock { Type = "text" }; acc.Blocks[idx] = b; }
                        Dictionary<string, object> d = Json.Obj(ev, "delta");
                        string dt = Json.Str(d, "type") ?? "";
                        if (dt == "text_delta")
                        {
                            string t = Json.Str(d, "text");
                            if (string.IsNullOrEmpty(t)) continue;
                            b.Text.Append(t);
                            if (onContent != null) { try { onContent(t); } catch (Exception ex) { DebugLog.Write("assistant: stream consumer: " + ex.Message); } }
                        }
                        else if (dt == "input_json_delta") b.Args.Append(Json.Str(d, "partial_json") ?? "");
                        else if (dt == "thinking_delta") b.Thinking.Append(Json.Str(d, "thinking") ?? "");
                        else if (dt == "signature_delta") b.Signature = (b.Signature ?? "") + (Json.Str(d, "signature") ?? "");
                    }
                    else if (type == "message_delta")
                    {
                        string sr = Json.Str(Json.Obj(ev, "delta"), "stop_reason");
                        if (!string.IsNullOrEmpty(sr)) acc.StopReason = sr;
                        acc.Usage(Json.Obj(ev, "usage"));
                    }
                    else if (type == "error")
                    {
                        Dictionary<string, object> err = Json.Obj(ev, "error") ?? ev;
                        string et = Json.Str(err, "type"), em = Json.Str(err, "message");
                        var e = new OpenAIException(ClaudeFriendly(et == "overloaded_error" ? 529 : 500, et, em, model), "claude stream error: " + et + ": " + em);
                        e.Status = et == "overloaded_error" ? 529 : 500; e.Code = et; e.ApiMessage = em;
                        throw e;
                    }
                    else if (type == "message_stop") { acc.Stopped = true; break; }
                }
            }
        }

        static Dictionary<string, object> FromClaude(ClaudeAcc acc)
        {
            List<object> content = acc.Content();
            var text = new StringBuilder();
            var calls = new List<object>();
            foreach (object o in content)
            {
                var b = o as Dictionary<string, object>;
                string type = Json.Str(b, "type");
                if (type == "text") text.Append(Json.Str(b, "text") ?? "");
                else if (type == "tool_use")
                {
                    var fn = new Dictionary<string, object>();
                    fn["name"] = Json.Str(b, "name") ?? "";
                    object input;
                    fn["arguments"] = Json.Write(b.TryGetValue("input", out input) && input != null ? input : new Dictionary<string, object>());
                    var tc = new Dictionary<string, object>();
                    tc["id"] = Json.Str(b, "id") ?? ("call_" + Guid.NewGuid().ToString("N").Substring(0, 12));
                    tc["type"] = "function";
                    tc["function"] = fn;
                    calls.Add(tc);
                }
            }
            var msg = new Dictionary<string, object>();
            msg["role"] = "assistant";
            msg["content"] = text.Length > 0 ? text.ToString() : null;
            if (acc.StopReason == "refusal" && text.Length == 0) msg["refusal"] = "i'd rather not help with that one.";
            if (calls.Count > 0) msg["tool_calls"] = calls.ToArray();
            msg["_claude"] = content;
            string finish = calls.Count > 0 ? "tool_calls" : acc.StopReason == "max_tokens" ? "length" : "stop";
            var ch = new Dictionary<string, object>();
            ch["index"] = 0;
            ch["message"] = msg;
            ch["finish_reason"] = finish;
            var usage = new Dictionary<string, object>();
            usage["prompt_tokens"] = acc.Input + acc.CacheRead + acc.CacheWrite;
            usage["completion_tokens"] = acc.Output;
            usage["prompt_tokens_details"] = new Dictionary<string, object> { { "cached_tokens", acc.CacheRead } };
            var result = new Dictionary<string, object>();
            result["choices"] = new object[] { ch };
            result["usage"] = usage;
            result["model"] = acc.Model;
            return result;
        }

        // ------------------------------------------------------------------ errors

        OpenAIException MapClaude(WebException ex, string model)
        {
            if (aborted || ex.Status == WebExceptionStatus.RequestCanceled) return AbortedException();
            var hr = ex.Response as HttpWebResponse;
            if (hr == null) return Map(ex, model);   // no answer at all: the same network lines as openai's
            int status = (int)hr.StatusCode;
            string text = "";
            try { text = ReadAll(hr); } catch { }
            finally { try { hr.Close(); } catch { } }
            string msg = null, type = null;
            try
            {
                Dictionary<string, object> err = Json.Obj(Json.Obj(text), "error");
                if (err != null) { msg = Json.Str(err, "message"); type = Json.Str(err, "type"); }
            }
            catch { }
            if (string.IsNullOrEmpty(msg)) msg = Trunc(text, 200);
            var e = new OpenAIException(ClaudeFriendly(status, type, msg, model), "claude HTTP " + status + " " + (type ?? "") + ": " + msg);
            e.Status = status; e.Code = type; e.ApiMessage = msg;
            return e;
        }

        static string ClaudeFriendly(int status, string type, string msg, string model)
        {
            string m = ((msg ?? "") + " " + (type ?? "")).ToLowerInvariant();
            if (status == 401) return "my claude key didn't work. it's ANTHROPIC_API_KEY in synaptic's .env.";
            if (m.Contains("credit balance") || m.Contains("billing")) return "the claude credit is used up for now. 1 out of 7.";
            if (status == 529 || m.Contains("overloaded")) return "claude is swamped right now. try again in a bit.";
            if (status == 429) return "claude says slow down. give it a few seconds.";
            if (status == 404 && m.Contains("model")) return "claude doesn't know a model called \"" + model + "\". pick another one in settings.";
            if (status == 413) return "that was too big to send. maybe a smaller screenshot?";
            if (status == 400 && (m.Contains("prompt is too long") || m.Contains("context"))) return "that was too much to carry at once. type /new to start fresh.";
            return FriendlyFor(status, type, type, msg, model);
        }

        /// <summary>One free call (the model's page): null if the claude key works and knows the model, else what's wrong.</summary>
        public static string CheckClaudeModel(string model)
        {
            string key = ClaudeKey.Get();
            if (key.Length == 0) return "there's no claude key. add ANTHROPIC_API_KEY to " + ClaudeKey.EnvFilePath + ".";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/models/" + Uri.EscapeDataString(model));
                req.Timeout = 20000;
                req.Headers["x-api-key"] = key;
                req.Headers["anthropic-version"] = ClaudeVersion;
                using (req.GetResponse()) { }
                return null;
            }
            catch (WebException ex)
            {
                OpenAIException e = new OpenAIClient(null, "").MapClaude(ex, model);
                return e.Friendly;
            }
        }

        // ------------------------------------------------------------------ warm up

        static DateTime lastClaudeWarmUtc = DateTime.MinValue;

        /// <summary>Opens the TLS connection to the Claude API ahead of a question (free: it only reads the model's page).</summary>
        public static void PrewarmClaude(string model)
        {
            lock (prewarmGate)
            {
                if ((DateTime.UtcNow - lastClaudeWarmUtc).TotalSeconds < 60) return;
                lastClaudeWarmUtc = DateTime.UtcNow;
            }
            var t = new System.Threading.Thread(delegate()
            {
                try
                {
                    string key = ClaudeKey.Get();
                    if (key.Length == 0) return;
                    var req = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/v1/models/" + Uri.EscapeDataString(model ?? "claude-haiku-5-5"));
                    req.Timeout = 5000;
                    req.Headers["x-api-key"] = key;
                    req.Headers["anthropic-version"] = ClaudeVersion;
                    using (var r = req.GetResponse()) { }
                }
                catch { }   // only warming the connection
            });
            t.IsBackground = true;
            t.Name = "bestie-prewarm-claude";
            t.Start();
        }
    }
}
