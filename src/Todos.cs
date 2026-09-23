using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bestie
{
    public class TodoItem
    {
        public string Text;
        public bool Done;
        public DateTime? DoneOn;
    }

    /// <summary>The to-do list: a plain markdown checklist in Documents\Bestie\todo.md, so you can edit it by hand too.
    /// Thread-safe; Changed fires (on whatever thread made the change) after every save.</summary>
    public static class Todos
    {
        static readonly object gate = new object();
        public static event Action Changed;
        /// <summary>An item was checked off (for the creature to react to).</summary>
        public static event Action<TodoItem> Completed;

        public static string FilePath { get { Home.Init(); return Path.Combine(Home.Root, "todo.md"); } }

        static readonly Regex Line = new Regex(@"^\s*[-*]\s*\[( |x|X)\]\s*(.*?)\s*(?:_\(done ([0-9-]+)\)_)?\s*$");

        public static List<TodoItem> Load()
        {
            lock (gate)
            {
                var list = new List<TodoItem>();
                try
                {
                    if (!File.Exists(FilePath)) return list;
                    foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        var m = Line.Match(raw);
                        if (!m.Success || m.Groups[2].Value.Length == 0) continue;
                        DateTime d;
                        list.Add(new TodoItem
                        {
                            Text = m.Groups[2].Value,
                            Done = m.Groups[1].Value != " ",
                            DoneOn = m.Groups[3].Success && DateTime.TryParseExact(m.Groups[3].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : (DateTime?)null
                        });
                    }
                }
                catch (Exception ex) { DebugLog.Write("todo: couldn't read the list: " + ex.Message); }
                return list;
            }
        }

        static void Save(List<TodoItem> items)
        {
            // finished things stay a week, then quietly leave
            items = items.Where(i => !i.Done || !i.DoneOn.HasValue || (DateTime.Today - i.DoneOn.Value).TotalDays <= 7).ToList();
            var sb = new StringBuilder("# to-do\r\n\r\n");
            foreach (var i in items.Where(i => !i.Done)) sb.Append("- [ ] ").Append(i.Text).Append("\r\n");
            foreach (var i in items.Where(i => i.Done))
                sb.Append("- [x] ").Append(i.Text).Append(i.DoneOn.HasValue ? " _(done " + i.DoneOn.Value.ToString("yyyy-MM-dd") + ")_" : "").Append("\r\n");
            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        static void Notify()
        {
            var c = Changed;
            if (c != null) try { c(); } catch (Exception ex) { DebugLog.Write("todo: " + ex.Message); }
        }

        public static TodoItem Add(string text)
        {
            text = Clean(text);
            if (text.Length == 0) return null;
            TodoItem item;
            lock (gate)
            {
                var list = Load();
                item = list.FirstOrDefault(i => !i.Done && i.Text.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (item != null) return item;   // already on the list
                item = new TodoItem { Text = text };
                list.Add(item);
                Save(list);
            }
            Notify();
            return item;
        }

        /// <summary>Find by 1-based number (open items first, as listed) or by text.</summary>
        static TodoItem Find(List<TodoItem> list, string match, bool openOnly)
        {
            match = (match ?? "").Trim();
            var pool = (openOnly ? list.Where(i => !i.Done) : list.OrderBy(i => i.Done)).ToList();
            int n;
            if (int.TryParse(match.TrimStart('#'), out n) && n >= 1 && n <= pool.Count) return pool[n - 1];
            return pool.FirstOrDefault(i => i.Text.Equals(match, StringComparison.OrdinalIgnoreCase))
                ?? pool.FirstOrDefault(i => i.Text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0 && match.Length > 1);
        }

        public static TodoItem SetDone(string match, bool done)
        {
            TodoItem item;
            lock (gate)
            {
                var list = Load();
                item = Find(list, match, done);
                if (item == null || item.Done == done) return item;
                item.Done = done;
                item.DoneOn = done ? DateTime.Today : (DateTime?)null;
                Save(list);
            }
            Notify();
            if (done) { var c = Completed; if (c != null) try { c(item); } catch { } }
            return item;
        }

        public static TodoItem Remove(string match)
        {
            TodoItem item;
            lock (gate)
            {
                var list = Load();
                item = Find(list, match, false);
                if (item == null) return null;
                list.Remove(item);
                Save(list);
            }
            Notify();
            return item;
        }

        public static string Describe()
        {
            var list = Load();
            var open = list.Where(i => !i.Done).ToList();
            var sb = new StringBuilder();
            if (open.Count == 0) sb.Append("the to-do list is empty.");
            else
            {
                sb.Append(open.Count + " to do:");
                for (int i = 0; i < open.Count; i++) sb.Append("\n" + (i + 1) + ". " + open[i].Text);
            }
            var done = list.Where(i => i.Done && i.DoneOn == DateTime.Today).ToList();
            if (done.Count > 0) sb.Append("\ndone today: " + string.Join("; ", done.Select(i => i.Text)));
            return sb.ToString();
        }

        static string Clean(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim().TrimStart('-', '*', ' ');
            if (s.StartsWith("[ ]") || s.StartsWith("[x]")) s = s.Substring(3).Trim();
            return s.Length > 200 ? s.Substring(0, 200) : s;
        }
    }
}
