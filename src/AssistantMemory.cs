// AssistantMemory.cs - the assistant's files in the creature's folder. C# 5 only.
//   memory\about-you.md   facts the user shared ("- likes pickles _(2026-09-22)_")
//   memory\*.md           anything else the user keeps there (read into the prompt, capped)
//   notes\<slug>.md       notes written on request
//   chats\yyyy-MM-dd.md   transcripts
// Thread-safe (one lock for all writes).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Bestie
{
    internal static class AssistantMemory
    {
        static readonly object gate = new object();
        static readonly Encoding Utf8 = new UTF8Encoding(false);
        public const string AboutYouFile = "about-you.md";
        const string AboutHeader = "# about you";
        const string AboutBlurb = "things you told me. edit freely, i read this before we talk.";

        public static string AboutYouPath { get { Home.Init(); return Path.Combine(Home.MemoryDir, AboutYouFile); } }

        static void EnsureAboutYou()
        {
            string p = AboutYouPath;
            if (!File.Exists(p)) File.WriteAllText(p, AboutHeader + "\r\n\r\n" + AboutBlurb + "\r\n\r\n", Utf8);
        }

        public static string OneLine(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { if (!space && sb.Length > 0) sb.Append(' '); space = true; }
                else { sb.Append(c); space = false; }
            }
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------ remember / forget

        public static string Remember(string fact)
        {
            fact = OneLine(fact).TrimStart('-', '*', ' ').Trim();
            if (fact.Length == 0) return "nothing to remember.";
            if (fact.Length > 300) fact = fact.Substring(0, 300);
            lock (gate)
            {
                EnsureAboutYou();
                string path = AboutYouPath;
                string existing = File.ReadAllText(path, Encoding.UTF8);
                foreach (string line in existing.Split('\n'))
                {
                    string l = StripDate(line.Trim().TrimStart('-', '*', ' ').Trim());
                    if (l.Equals(fact, StringComparison.OrdinalIgnoreCase)) return "already knew that.";
                }
                string entry = "- " + fact + " _(" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")_\r\n";
                if (existing.Length > 0 && !existing.EndsWith("\n")) entry = "\r\n" + entry;
                File.AppendAllText(path, entry, Utf8);
            }
            return "remembered.";
        }

        static string StripDate(string line)
        {
            int i = line.LastIndexOf(" _(", StringComparison.Ordinal);
            return i > 0 && line.EndsWith(")_") ? line.Substring(0, i).Trim() : line;
        }

        /// <summary>Removes memory lines containing text. Refuses if it would remove more than 5.</summary>
        public static string Forget(string text, out int removed)
        {
            removed = 0;
            text = OneLine(text);
            if (text.Length < 3) return "tell me a bit more of what to forget.";
            lock (gate)
            {
                Home.Init();
                var hits = new List<KeyValuePair<string, string>>();   // file -> line
                string[] files = Directory.GetFiles(Home.MemoryDir, "*.md");
                foreach (string f in files)
                {
                    foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#") || t == AboutBlurb) continue;
                        if (t.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(new KeyValuePair<string, string>(f, line));
                    }
                }
                if (hits.Count == 0) return "couldn't find \"" + text + "\" in my memory.";
                if (hits.Count > 5)
                    return "that matches " + hits.Count + " lines, which is a lot. be more specific? e.g.: " +
                        string.Join(" | ", hits.Take(5).Select(h => OpenAIClient.Trunc(h.Value.Trim(), 80)).ToArray());
                var gone = new List<string>();
                foreach (var group in hits.GroupBy(h => h.Key))
                {
                    var drop = new HashSet<string>(group.Select(h => h.Value));
                    var keep = new List<string>();
                    foreach (string line in File.ReadAllLines(group.Key, Encoding.UTF8))
                    {
                        if (drop.Contains(line)) { gone.Add(line.Trim()); removed++; }
                        else keep.Add(line);
                    }
                    File.WriteAllText(group.Key, string.Join("\r\n", keep.ToArray()) + "\r\n", Utf8);
                }
                return "forgot: " + string.Join(" | ", gone.Select(g => OpenAIClient.Trunc(StripDate(g.TrimStart('-', '*', ' ')), 80)).ToArray());
            }
        }

        /// <summary>Memory files for the system prompt: about-you.md first, capped.</summary>
        public static string ForPrompt(int cap)
        {
            try
            {
                Home.Init();
                var files = Directory.GetFiles(Home.MemoryDir, "*.md").ToList();
                files.Sort(StringComparer.OrdinalIgnoreCase);
                int about = files.FindIndex(f => Path.GetFileName(f).Equals(AboutYouFile, StringComparison.OrdinalIgnoreCase));
                if (about > 0) { string a = files[about]; files.RemoveAt(about); files.Insert(0, a); }
                var sb = new StringBuilder();
                foreach (string f in files)
                {
                    var lines = File.ReadAllLines(f, Encoding.UTF8)
                        .Where(l => l.Trim().Length > 0 && l.Trim() != AboutHeader && l.Trim() != AboutBlurb).ToArray();
                    if (lines.Length == 0) continue;
                    sb.Append("(" + Path.GetFileName(f) + ")\n");
                    // a long memory keeps its newest lines (they're at the end); the oldest ones wait in the file
                    int room = cap * 3 / 4, used = 0, from = lines.Length;
                    while (from > 0 && used + lines[from - 1].Length + 1 <= room) { from--; used += lines[from].Length + 1; }
                    if (from > 0) { sb.Append("(" + from + " older lines are in " + Path.GetFileName(f) + ")\n"); lines = lines.Skip(from).ToArray(); }
                    foreach (string l in lines) sb.Append(l.TrimEnd()).Append('\n');
                    if (sb.Length > cap) break;
                }
                string s = sb.ToString().Trim();
                if (s.Length > cap) s = s.Substring(0, cap) + "\n(…more in the memory folder)";
                return s;
            }
            catch (Exception ex) { DebugLog.Write("assistant: memory read: " + ex.Message); return ""; }
        }

        // ------------------------------------------------------------------ notes

        public static string Slug(string title)
        {
            var sb = new StringBuilder();
            foreach (char c in (title ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                if (sb.Length >= 60) break;
            }
            string s = sb.ToString().Trim('-');
            return s.Length == 0 ? "note-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) : s;
        }

        public static string WriteNote(string title, string content, bool append, out string relPath)
        {
            title = OneLine(title);
            if (title.Length == 0) title = "note";
            string slug = Slug(title);
            relPath = "notes/" + slug + ".md";
            content = (content ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n").TrimEnd();
            if (content.Length > 20000) content = content.Substring(0, 20000) + "\r\n(…cut off)";
            lock (gate)
            {
                Home.Init();
                string path = Path.Combine(Home.NotesDir, slug + ".md");
                bool exists = File.Exists(path);
                if (exists && append)
                {
                    File.AppendAllText(path, "\r\n" + content + "\r\n", Utf8);
                    return "added to " + relPath + ".";
                }
                File.WriteAllText(path, "# " + title + "\r\n\r\n" + content + "\r\n", Utf8);
                return (exists ? "rewrote " : "saved ") + relPath + ".";
            }
        }

        static string FindNote(string name)
        {
            Home.Init();
            var files = Directory.GetFiles(Home.NotesDir).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count == 0) return null;
            string raw = (name ?? "").Trim();
            if (raw.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || raw.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) raw = Path.GetFileNameWithoutExtension(raw);
            int slash = raw.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) raw = raw.Substring(slash + 1);
            string want = Slug(raw);
            foreach (string f in files) if (Path.GetFileNameWithoutExtension(f).Equals(want, StringComparison.OrdinalIgnoreCase)) return f;
            foreach (string f in files) if (Path.GetFileNameWithoutExtension(f).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return f;
            string best = null; double bestScore = 0;
            foreach (string f in files)
            {
                double sc = StartMenuApps.Similarity(want, Path.GetFileNameWithoutExtension(f).ToLowerInvariant());
                if (sc > bestScore) { bestScore = sc; best = f; }
            }
            return bestScore >= 0.6 ? best : null;
        }

        public static string ReadNote(string name, int cap)
        {
            string f = FindNote(name);
            if (f == null) return "no note called \"" + name + "\". " + ListNotes();
            string text = File.ReadAllText(f, Encoding.UTF8);
            if (text.Length > cap) text = text.Substring(0, cap) + "\n(…cut off, it's long)";
            return "notes/" + Path.GetFileName(f) + ":\n" + text;
        }

        public static string ListNotes()
        {
            Home.Init();
            var files = new DirectoryInfo(Home.NotesDir).GetFiles()
                .Where(f => f.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime).Take(50).ToList();
            if (files.Count == 0) return "no notes yet.";
            var sb = new StringBuilder("notes (newest first):");
            foreach (var f in files) sb.Append("\n- " + f.Name + " (" + f.LastWriteTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ journal

        public static string ReadJournal(string date, int cap)
        {
            DateTime day = DateTime.Now.Date;
            string d = (date ?? "").Trim().ToLowerInvariant();
            if (d == "yesterday") day = day.AddDays(-1);
            else if (d.Length > 0 && d != "today")
            {
                DateTime parsed;
                if (DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) day = parsed;
                else if (DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) day = parsed.Date;
            }
            string path = Journal.PathFor(day);
            if (!File.Exists(path))
            {
                var days = Directory.GetFiles(Home.JournalDir, "*.md").Select(f => Path.GetFileNameWithoutExtension(f))
                    .OrderByDescending(n => n, StringComparer.Ordinal).Take(10).ToArray();
                return "no journal for " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "." +
                    (days.Length > 0 ? " i have: " + string.Join(", ", days) : " i haven't written any yet.");
            }
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length > cap)
            {
                text = text.Substring(text.Length - cap);
                int nl = text.IndexOf('\n');
                if (nl >= 0 && nl < 200) text = text.Substring(nl + 1);
                text = "(…earlier lines left out)\n" + text;
            }
            return "my journal for " + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " (my own observations):\n" + text;
        }

        // ------------------------------------------------------------------ search

        public static string Search(string query, int maxHits)
        {
            query = OneLine(query);
            if (query.Length < 2) return "search for something a bit longer.";
            Home.Init();
            var files = new List<FileInfo>();
            foreach (string dir in new[] { Home.MemoryDir, Home.NotesDir, Home.JournalDir, Home.ChatsDir })
            {
                try { files.AddRange(new DirectoryInfo(dir).GetFiles("*.md")); } catch { }
                try { files.AddRange(new DirectoryInfo(dir).GetFiles("*.txt")); } catch { }
            }
            files = files.OrderByDescending(f => f.LastWriteTime).ToList();
            var hits = Scan(files, line => line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0, maxHits);
            if (hits.Count == 0)
            {
                string[] words = query.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToArray();
                if (words.Length > 1)
                    hits = Scan(files, line => { string l = line.ToLowerInvariant(); return words.All(w => l.Contains(w)); }, maxHits);
            }
            if (hits.Count == 0) return "nothing matched \"" + query + "\".";
            return "matches (file:line):\n" + string.Join("\n", hits.ToArray());
        }

        static List<string> Scan(List<FileInfo> files, Func<string, bool> match, int maxHits)
        {
            var hits = new List<string>();
            foreach (var f in files)
            {
                string[] lines;
                try { lines = File.ReadAllLines(f.FullName, Encoding.UTF8); } catch { continue; }
                for (int i = 0; i < lines.Length && hits.Count < maxHits; i++)
                    if (match(lines[i])) hits.Add(RelPath(f.FullName) + ":" + (i + 1) + ": " + OpenAIClient.Trunc(lines[i].Trim(), 160));
                if (hits.Count >= maxHits) break;
            }
            return hits;
        }

        public static string RelPath(string full)
        {
            Home.Init();
            string root = Home.Root.TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length).Replace('\\', '/') : full;
        }

        // ------------------------------------------------------------------ chat transcripts

        /// <summary>Append "**who:** text" to today's chat file.</summary>
        public static void AppendChat(string who, string text)
        {
            AppendChatRaw("**" + who + ":** " + (text ?? "").Trim().Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n\r\n");
        }

        /// <summary>Append an italic action line ("_opened youtube.com_").</summary>
        public static void AppendChatNote(string note)
        {
            AppendChatRaw("_" + OneLine(note).Replace("_", " ") + "_\r\n\r\n");
        }

        static void AppendChatRaw(string text)
        {
            try
            {
                lock (gate)
                {
                    Home.Init();
                    DateTime now = DateTime.Now;
                    string path = Path.Combine(Home.ChatsDir, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md");
                    if (!File.Exists(path))
                        text = "# chats — " + now.ToString("dddd, MMMM d yyyy", CultureInfo.InvariantCulture).ToLowerInvariant() + "\r\n\r\n" + text;
                    File.AppendAllText(path, text, Utf8);
                }
            }
            catch (Exception ex) { DebugLog.Write("assistant: chat transcript: " + ex.Message); }
        }
    }
}
