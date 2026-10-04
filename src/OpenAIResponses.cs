// OpenAIResponses.cs - some models (gpt-6.1-*) only take function tools through /v1/responses, not
// /v1/chat/completions. The rest of Bestie speaks chat completions, so this translates a chat body into a
// Responses request and the answer back into the chat shape ({choices:[{message:{content, tool_calls}}]}),
// streaming included. C# 5 only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Bestie
{
    public sealed partial class OpenAIClient
    {
        /// <summary>Models known to need /responses for tools (others are found out from their first 400).</summary>
        static bool KnownResponsesOnly(string model)
        {
            return (model ?? "").StartsWith("gpt-6.1", StringComparison.OrdinalIgnoreCase);
        }

        bool UsesResponses(string model)
        {
            if (KnownResponsesOnly(model)) return true;
            lock (quirkGate)
            {
                List<string> k;
                return quirks.TryGetValue(baseUrl + "|" + model, out k) && k.Contains("responses");
            }
        }

        /// <summary>A 400 that says "use /v1/responses": remember it for this model and say so.</summary>
        bool LearnResponses(OpenAIException ex, string model)
        {
            string m = ((ex.ApiMessage ?? "") + " " + (ex.Param ?? "")).ToLowerInvariant();
            if (ex.Status != 400 || !m.Contains("/responses")) return false;
            DebugLog.Write("assistant: " + model + " wants the responses api; switching (" + Trunc(ex.ApiMessage, 160) + ")");
            lock (quirkGate)
            {
                List<string> known;
                if (!quirks.TryGetValue(baseUrl + "|" + model, out known)) { known = new List<string>(); quirks[baseUrl + "|" + model] = known; }
                if (!known.Contains("responses")) known.Add("responses");
            }
            return true;
        }

        /// <summary>A chat-completions body, answered through /responses. Returns the chat-completions shape.</summary>
        Dictionary<string, object> ViaResponses(Dictionary<string, object> chat, int timeoutMs, bool stream, Action<string> onContent, Action onToolCall)
        {
            string model = Json.Str(chat, "model") ?? "";
            Dictionary<string, object> rb = ToResponses(chat);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (stream)
                    {
                        rb["stream"] = true;
                        return ResponsesStream(rb, timeoutMs, model, onContent, onToolCall);
                    }
                    rb.Remove("stream");
                    Dictionary<string, object> r = FromResponses(PostJson("responses", rb, timeoutMs, model));
                    FeedWhole(r, onContent, onToolCall);
                    return r;
                }
                catch (OpenAIException ex)
                {
                    if (attempt >= 3 || ex.Status != 400) throw;
                    string m = ((ex.ApiMessage ?? "") + " " + (ex.Param ?? "")).ToLowerInvariant();
                    var reasoning = rb.ContainsKey("reasoning") ? rb["reasoning"] as Dictionary<string, object> : null;
                    int sv = m.IndexOf("supported values");
                    if (reasoning != null && m.Contains("reasoning") && sv >= 0)
                    {
                        // e.g. "does not support 'none' ... supported values are: 'low', 'medium'...": take the lightest
                        string cur = Json.Str(reasoning, "effort") ?? "";
                        string pick = new[] { "none", "minimal", "low", "medium" }.FirstOrDefault(v => v != cur && m.Substring(sv).Contains("'" + v + "'"));
                        if (pick == null) throw;
                        reasoning["effort"] = pick;
                        lock (quirkGate)
                        {
                            List<string> known;
                            if (!quirks.TryGetValue(baseUrl + "|" + model, out known)) { known = new List<string>(); quirks[baseUrl + "|" + model] = known; }
                            known.RemoveAll(q => q.StartsWith("reasoning="));
                            known.Add("reasoning=" + pick);
                        }
                        DebugLog.Write("assistant: adapting to reasoning '" + pick + "' for model " + model);
                        continue;
                    }
                    if (stream && m.Contains("stream")) { stream = false; continue; }
                    throw;
                }
            }
        }

        // ------------------------------------------------------------------ chat -> responses

        static Dictionary<string, object> ToResponses(Dictionary<string, object> chat)
        {
            string model = Json.Str(chat, "model") ?? "";
            var rb = new Dictionary<string, object>();
            rb["model"] = model;
            rb["store"] = false;   // nothing kept on their side; every round sends what it needs
            var input = new List<object>();
            string instructions = null;
            foreach (Dictionary<string, object> m in Each(chat, "messages"))
            {
                string role = Json.Str(m, "role") ?? "user";
                if (role == "system")
                {
                    if (instructions == null) { instructions = Text(m["content"]); continue; }
                    input.Add(Item("developer", Text(m["content"])));
                    continue;
                }
                if (role == "tool")
                {
                    var o = new Dictionary<string, object>();
                    o["type"] = "function_call_output";
                    o["call_id"] = Json.Str(m, "tool_call_id") ?? "";
                    o["output"] = Text(m.ContainsKey("content") ? m["content"] : "");
                    input.Add(o);
                    continue;
                }
                if (role == "assistant")
                {
                    string said = m.ContainsKey("content") ? Text(m["content"]) : "";
                    if (said.Length > 0) input.Add(Item("assistant", said));
                    foreach (Dictionary<string, object> tc in Each(m, "tool_calls"))
                    {
                        Dictionary<string, object> fn = Json.Obj(tc, "function");
                        var c = new Dictionary<string, object>();
                        c["type"] = "function_call";
                        c["call_id"] = Json.Str(tc, "id") ?? "";
                        c["name"] = Json.Str(fn, "name") ?? "";
                        c["arguments"] = Json.Str(fn, "arguments") ?? "{}";
                        input.Add(c);
                    }
                    continue;
                }
                // user: plain text, or text and images
                object content = m.ContainsKey("content") ? m["content"] : "";
                if (content is string) { input.Add(Item("user", (string)content)); continue; }
                var parts = new List<object>();
                foreach (object po in AsList(content))
                {
                    var p = po as Dictionary<string, object>;
                    if (p == null) continue;
                    string type = Json.Str(p, "type");
                    if (type == "text") parts.Add(new Dictionary<string, object> { { "type", "input_text" }, { "text", Json.Str(p, "text") ?? "" } });
                    else if (type == "image_url")
                    {
                        Dictionary<string, object> iu = Json.Obj(p, "image_url");
                        parts.Add(new Dictionary<string, object> { { "type", "input_image" }, { "image_url", Json.Str(iu, "url") ?? "" }, { "detail", Json.Str(iu, "detail") ?? "auto" } });
                    }
                }
                var um = new Dictionary<string, object>();
                um["role"] = "user";
                um["content"] = parts;
                input.Add(um);
            }
            if (instructions != null) rb["instructions"] = instructions;
            rb["input"] = input;

            var tools = new List<object>();
            foreach (Dictionary<string, object> t in Each(chat, "tools"))
            {
                Dictionary<string, object> fn = Json.Obj(t, "function");
                if (fn == null) continue;
                var rt = new Dictionary<string, object>();
                rt["type"] = "function";
                rt["name"] = Json.Str(fn, "name") ?? "";
                rt["description"] = Json.Str(fn, "description") ?? "";
                rt["parameters"] = fn.ContainsKey("parameters") ? fn["parameters"] : new Dictionary<string, object>();
                rt["strict"] = false;   // our parameters have optional fields
                tools.Add(rt);
            }
            if (tools.Count > 0)
            {
                rb["tools"] = tools;
                object choice;
                if (chat.TryGetValue("tool_choice", out choice))
                {
                    var forced = choice as Dictionary<string, object>;
                    if (forced != null) rb["tool_choice"] = new Dictionary<string, object> { { "type", "function" }, { "name", Json.Str(Json.Obj(forced, "function"), "name") ?? "" } };
                    else rb["tool_choice"] = choice;
                }
            }

            string effort = Json.Str(chat, "reasoning_effort");
            if (!string.IsNullOrEmpty(effort))
            {
                // gpt-6.1 thinks at least a little ("none" isn't offered); "low" keeps it quick
                if (effort == "none" && KnownResponsesOnly(model)) effort = "low";
                rb["reasoning"] = new Dictionary<string, object> { { "effort", effort } };
            }
            // its thinking counts against the output budget too, so leave room for it
            double max = Json.Num(chat, "max_completion_tokens", Json.Num(chat, "max_tokens", 0));
            if (max > 0) rb["max_output_tokens"] = (int)max + (rb.ContainsKey("reasoning") || KnownResponsesOnly(model) ? 3000 : 0);
            return rb;
        }

        static Dictionary<string, object> Item(string role, string text)
        {
            var d = new Dictionary<string, object>();
            d["role"] = role;
            d["content"] = text ?? "";
            return d;
        }

        static IEnumerable<object> AsList(object o)
        {
            var e = o as IEnumerable;
            if (e == null || o is string) yield break;
            foreach (object x in e) yield return x;
        }

        static IEnumerable<Dictionary<string, object>> Each(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) yield break;
            foreach (object o in AsList(v)) { var x = o as Dictionary<string, object>; if (x != null) yield return x; }
        }

        /// <summary>Message content as plain text (parts joined).</summary>
        static string Text(object content)
        {
            if (content == null) return "";
            var s = content as string;
            if (s != null) return s;
            var sb = new StringBuilder();
            foreach (object po in AsList(content))
            {
                var p = po as Dictionary<string, object>;
                string t = p != null ? Json.Str(p, "text") : null;
                if (!string.IsNullOrEmpty(t)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(t); }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ responses -> chat

        static Dictionary<string, object> FromResponses(Dictionary<string, object> r)
        {
            var text = new StringBuilder();
            var refusal = new StringBuilder();
            var calls = new List<object>();
            foreach (Dictionary<string, object> item in Each(r, "output"))
            {
                string type = Json.Str(item, "type");
                if (type == "message")
                {
                    foreach (Dictionary<string, object> c in Each(item, "content"))
                    {
                        string ct = Json.Str(c, "type");
                        if (ct == "output_text") text.Append(Json.Str(c, "text") ?? "");
                        else if (ct == "refusal") refusal.Append(Json.Str(c, "refusal") ?? "");
                    }
                }
                else if (type == "function_call")
                {
                    var fn = new Dictionary<string, object>();
                    fn["name"] = Json.Str(item, "name") ?? "";
                    fn["arguments"] = Json.Str(item, "arguments") ?? "{}";
                    var tc = new Dictionary<string, object>();
                    tc["id"] = Json.Str(item, "call_id") ?? ("call_" + Guid.NewGuid().ToString("N").Substring(0, 12));
                    tc["type"] = "function";
                    tc["function"] = fn;
                    calls.Add(tc);
                }
            }
            var msg = new Dictionary<string, object>();
            msg["role"] = "assistant";
            msg["content"] = text.Length > 0 ? text.ToString() : null;
            if (refusal.Length > 0) msg["refusal"] = refusal.ToString();
            if (calls.Count > 0) msg["tool_calls"] = calls.ToArray();
            string finish = calls.Count > 0 ? "tool_calls" : "stop";
            if (Json.Str(r, "status") == "incomplete" && Json.Str(Json.Obj(r, "incomplete_details"), "reason") == "max_output_tokens") finish = "length";
            var ch = new Dictionary<string, object>();
            ch["index"] = 0;
            ch["message"] = msg;
            ch["finish_reason"] = finish;
            var result = new Dictionary<string, object>();
            result["choices"] = new object[] { ch };
            if (r.ContainsKey("usage")) result["usage"] = r["usage"];
            return result;
        }

        Dictionary<string, object> ResponsesStream(Dictionary<string, object> rb, int timeoutMs, string model, Action<string> onContent, Action onToolCall)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(rb));
            HttpWebRequest req = Make("responses", "POST", timeoutMs);
            req.ContentType = "application/json";
            req.Accept = "text/event-stream";
            lock (gate)
            {
                if (aborted) throw AbortedException();
                current = req;
            }
            try
            {
                req.ContentLength = bytes.Length;
                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if ((resp.ContentType ?? "").IndexOf("event-stream", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Dictionary<string, object> whole = null;
                        string body = ReadAll(resp);
                        try { whole = Json.Obj(body); } catch { }
                        if (whole == null) throw new OpenAIException("the big outside said something i couldn't read.", "unparseable response: " + Trunc(body, 200));
                        Dictionary<string, object> r = FromResponses(whole);
                        FeedWhole(r, onContent, onToolCall);
                        return r;
                    }
                    var text = new StringBuilder();
                    bool toldToolCall = false;
                    Dictionary<string, object> final = null;
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (!line.StartsWith("data:")) continue;
                            string data = line.Substring(5).Trim();
                            if (data.Length == 0 || data == "[DONE]") continue;
                            Dictionary<string, object> ev = null;
                            try { ev = Json.Obj(data); } catch { }
                            if (ev == null) continue;
                            string type = Json.Str(ev, "type") ?? "";
                            if (type == "response.output_text.delta")
                            {
                                string d = Json.Str(ev, "delta");
                                if (!string.IsNullOrEmpty(d))
                                {
                                    text.Append(d);
                                    if (onContent != null) { try { onContent(d); } catch (Exception ex) { DebugLog.Write("assistant: stream consumer: " + ex.Message); } }
                                }
                            }
                            else if (type == "response.output_item.added")
                            {
                                if (Json.Str(Json.Obj(ev, "item"), "type") == "function_call" && !toldToolCall && onToolCall != null) { toldToolCall = true; try { onToolCall(); } catch { } }
                            }
                            else if (type == "response.completed" || type == "response.incomplete")
                            {
                                final = Json.Obj(ev, "response");
                            }
                            else if (type == "response.failed" || type == "error")
                            {
                                Dictionary<string, object> err = Json.Obj(Json.Obj(ev, "response"), "error") ?? Json.Obj(ev, "error") ?? ev;
                                throw new OpenAIException(FriendlyFor(500, Json.Str(err, "code"), Json.Str(err, "type"), Json.Str(err, "message"), model),
                                    "stream error: " + Json.Str(err, "message"));
                            }
                        }
                    }
                    if (final != null) return FromResponses(final);
                    // no completed event: make do with the text that came through
                    var msg = new Dictionary<string, object> { { "role", "assistant" }, { "content", text.Length > 0 ? text.ToString() : null } };
                    return new Dictionary<string, object> { { "choices", new object[] { new Dictionary<string, object> { { "index", 0 }, { "message", msg }, { "finish_reason", "stop" } } } } };
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
    }
}
