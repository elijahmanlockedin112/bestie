// Watches.cs - "tell me when the render's done (and send it to my phone)": it keeps an eye on a process, a log file or an
// output file in the background and says so the moment it's finished: on the computer, on the phone when they're away,
// and on the echo. Optionally sends the finished file to the phone. Nothing is changed or stopped. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Bestie
{
    internal sealed partial class AssistantTools
    {
        static readonly List<string> watching = new List<string>();

        void AddWatchTools(List<object> t)
        {
            t.Add(Tool("watch", "tell them the moment something finishes, without them checking: a render, an export, a download, a build, an install. "
                + "it waits in the background (up to 12 hours) for a program to close, a line to show up in a log, or a file to finish writing, then announces "
                + "it (computer, phone, echo) and can send the finished file to their phone. one call; don't check on it yourself. list shows what's being watched.", R(),
                P("label", "string", "what it is, as they'd say it: 'the blender render', 'the download'"),
                P("process", "string", "wait until this program closes, e.g. 'blender', 'ffmpeg' (process name, no .exe)"),
                P("log_file", "string", "a log file to watch (full path)"),
                P("text", "string", "with log_file: the words that mean it's done, e.g. 'Finished' or 'Video append frame 4177'"),
                P("file", "string", "an output file: done once it exists and stops growing for 30 s (also what send_to_phone sends)"),
                P("send_to_phone", "boolean", "send the finished file to their phone"),
                PE("action", "watch (default) or list", new[] { "watch", "list" })));
        }

        AssistantToolResult WatchTool(string name, Dictionary<string, object> a)
        {
            if (name != "watch") return null;
            if (Json.Str(a, "action") == "list")
                lock (watching) return new AssistantToolResult(watching.Count == 0 ? "nothing's being watched." : "watching: " + string.Join("; ", watching.ToArray()) + ".");
            string label = (Json.Str(a, "label") ?? "it").Trim();
            string proc = (Json.Str(a, "process") ?? "").Trim();
            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) proc = proc.Substring(0, proc.Length - 4);
            string log = (Json.Str(a, "log_file") ?? "").Trim(), text = (Json.Str(a, "text") ?? "").Trim(), file = (Json.Str(a, "file") ?? "").Trim();
            bool send = Json.Bool(a, "send_to_phone", false);
            if (proc.Length == 0 && log.Length == 0 && file.Length == 0) return new AssistantToolResult("watch what? give a process, a log file (with the text that means done) or an output file.");
            if (proc.Length > 0 && Process.GetProcessesByName(proc).Length == 0) return new AssistantToolResult("there's no " + proc + " running right now, so it's either done or never started.");
            if (log.Length > 0 && (text.Length == 0 || !File.Exists(log))) return new AssistantToolResult(text.Length == 0 ? "with a log file, say what text means it's done." : "there's no log at " + log + ".");
            if (send && file.Length == 0) return new AssistantToolResult("to send it to their phone i need the output file's path (find_files can find it).");
            if (DryRun) return new AssistantToolResult("watching.");
            string what = label + (proc.Length > 0 ? " (until " + proc + " closes)" : log.Length > 0 ? " (until its log says \"" + text + "\")" : " (until " + Path.GetFileName(file) + " is written)");
            lock (watching) watching.Add(what);
            new Thread(delegate() { Watch(label, what, proc, log, text, file, send); }) { IsBackground = true, Name = "bestie-watch" }.Start();
            DebugLog.Write("watch: " + what);
            return new AssistantToolResult("watching " + what + ". you'll announce it the moment it's done" + (send ? " and send " + Path.GetFileName(file) + " to their phone" : "")
                + ". tell them in a few words.", "watching " + label, null);
        }

        void Watch(string label, string what, string proc, string log, string text, string file, bool send)
        {
            var sw = Stopwatch.StartNew();
            long lastSize = -1; DateTime stableSince = DateTime.MaxValue;
            bool done = false, timedOut = false;
            try
            {
                while (!done)
                {
                    Thread.Sleep(5000);
                    if (sw.Elapsed.TotalHours > 12) { timedOut = true; break; }
                    if (proc.Length > 0) { done = Process.GetProcessesByName(proc).Length == 0; continue; }
                    if (log.Length > 0) { done = LogSays(log, text); continue; }
                    if (File.Exists(file))
                    {
                        long size = new FileInfo(file).Length;
                        if (size != lastSize) { lastSize = size; stableSince = DateTime.UtcNow; }
                        else if (size > 0 && (DateTime.UtcNow - stableSince).TotalSeconds >= 30) done = true;
                    }
                }
            }
            catch (Exception ex) { DebugLog.Write("watch: " + ex.Message); }
            finally { lock (watching) watching.Remove(what); }
            if (timedOut) { module.Announce("i stopped watching " + label + " after 12 hours. it never finished."); return; }
            if (!done) return;
            string took = sw.Elapsed.TotalMinutes >= 90 ? (int)sw.Elapsed.TotalHours + " h " + sw.Elapsed.Minutes + " min" : (int)Math.Max(1, sw.Elapsed.TotalMinutes) + " min";
            string line = label + " is done" + (proc.Length > 0 ? " (" + proc + " closed)" : "") + ". i watched it for " + took + ".";
            if (file.Length > 0 && send && File.Exists(file))
            {
                // the output may still be settling right after the program closes
                Thread.Sleep(3000);
                PhoneServer srv = PhoneServer.Current;
                string problem = srv == null ? "the phone app isn't running" : srv.SendFileToPhone(file, true);
                line += problem == null ? " " + Path.GetFileName(file) + " is on your phone." : " couldn't send it to your phone: " + problem + ".";
            }
            else if (file.Length > 0 && send) line += " but " + Path.GetFileName(file) + " isn't there, so nothing went to your phone.";
            DebugLog.Write("watch: done: " + what);
            Journal.Add(label + " finished (i watched it for " + took + ").");
            module.Announce(line);
            module.AlexaNotice(line, false, true);
        }

        static bool LogSays(string path, string text)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int take = (int)Math.Min(fs.Length, 64 * 1024);
                    fs.Seek(fs.Length - take, SeekOrigin.Begin);
                    var buf = new byte[take];
                    int read = 0;
                    while (read < take) { int n = fs.Read(buf, read, take - read); if (n <= 0) break; read += n; }
                    return Encoding.UTF8.GetString(buf, 0, read).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }
    }
}
