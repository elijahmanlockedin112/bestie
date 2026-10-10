// Plans.cs - "this weekend i want to do x, y and z": the assistant puts them in order and on days (the plan tool), each
// day's list pops up as a reminder (that morning, or when they said), "i did x" checks it off and says what's next, and
// the live context and the second brain show what's left. Kept in .state\plans.json. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bestie
{
    internal sealed class PlanItem
    {
        public string Id, Text, Day, Time = "", Why = "";   // Day: yyyy-MM-dd, Time: HH:mm or ""
        public string ReminderId = "", NudgeId = "";        // the day's list popping up; its own "time for x"
        public int Order;
        public bool Done;
    }

    internal static class Plans
    {
        static readonly object gate = new object();
        static string FilePath { get { return Home.State("plans.json"); } }
        static readonly string[] DayNames = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

        public static List<PlanItem> All()
        {
            lock (gate) return Load();
        }

        /// <summary>Today and the days after it that have something planned, in order.</summary>
        public static List<KeyValuePair<DateTime, List<PlanItem>>> Upcoming()
        {
            string today = DateTime.Today.ToString("yyyy-MM-dd");
            return All().Where(p => string.CompareOrdinal(p.Day, today) >= 0).GroupBy(p => p.Day).OrderBy(g => g.Key)
                .Select(g => new KeyValuePair<DateTime, List<PlanItem>>(DateTime.ParseExact(g.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture), g.OrderBy(p => p.Order).ToList()))
                .ToList();
        }

        static List<PlanItem> Load()
        {
            var list = new List<PlanItem>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                object parsed = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                var arr = parsed as object[] ?? (parsed is System.Collections.ArrayList ? ((System.Collections.ArrayList)parsed).ToArray() : new object[0]);
                string keepFrom = DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd");
                foreach (object o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var p = new PlanItem { Id = Json.Str(d, "id") ?? "", Text = Json.Str(d, "text") ?? "", Day = Json.Str(d, "day") ?? "", Time = Json.Str(d, "time") ?? "",
                        Why = Json.Str(d, "why") ?? "", ReminderId = Json.Str(d, "reminder") ?? "", NudgeId = Json.Str(d, "nudge") ?? "",
                        Order = (int)Json.Num(d, "order", 0), Done = Json.Bool(d, "done", false) };
                    if (p.Text.Length > 0 && string.CompareOrdinal(p.Day, keepFrom) >= 0) list.Add(p);   // two weeks of history, then it's gone
                }
            }
            catch (Exception ex) { DebugLog.Write("plans: " + ex.Message); }
            return list;
        }

        static void Save(List<PlanItem> items)
        {
            var arr = items.OrderBy(p => p.Day).ThenBy(p => p.Order).Select(p => (object)new Dictionary<string, object> {
                { "id", p.Id }, { "text", p.Text }, { "day", p.Day }, { "time", p.Time }, { "why", p.Why }, { "reminder", p.ReminderId }, { "nudge", p.NudgeId }, { "order", p.Order }, { "done", p.Done } }).ToList();
            File.WriteAllText(FilePath, Json.Write(arr), Encoding.UTF8);
        }

        // ------------------------------------------------------------------ reading what they said

        /// <summary>"today", "tomorrow", "saturday" (the next one, today included), "2026-10-11".</summary>
        internal static DateTime? ParseDay(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            DateTime today = DateTime.Today;
            if (s == "" || s == "today" || s == "tonight") return today;
            if (s == "tomorrow") return today.AddDays(1);
            if (s.StartsWith("next ")) { DateTime? n = ParseDay(s.Substring(5)); return n.HasValue && n.Value == today ? today.AddDays(7) : n; }
            for (int i = 0; i < 7; i++)
                if (DayNames[i] == s || (s.Length >= 3 && DayNames[i].StartsWith(s)))
                    return today.AddDays(((i - (int)today.DayOfWeek) + 7) % 7);
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.Date;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out d)) return d.Date;
            return null;
        }

        /// <summary>"10", "10am", "4:30 pm", "16:00" -> "HH:mm", or "" if it isn't a time.</summary>
        internal static string ParseTime(string s)
        {
            Match m = Regex.Match((s ?? "").Trim().ToLowerInvariant(), @"^(\d{1,2})(?::(\d{2}))?\s*(a\.?m\.?|p\.?m\.?)?$");
            if (!m.Success) return "";
            int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            string ap = m.Groups[3].Value;
            if (ap.StartsWith("p") && h < 12) h += 12;
            if (ap.StartsWith("a") && h == 12) h = 0;
            if (ap.Length == 0 && h >= 1 && h <= 6) h += 12;   // "at 4" means the afternoon
            if (h > 23 || min > 59) return "";
            return h.ToString("00") + ":" + min.ToString("00");
        }

        static DateTime At(string day, string time)
        {
            DateTime d = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return time.Length == 5 ? d.AddHours(int.Parse(time.Substring(0, 2))).AddMinutes(int.Parse(time.Substring(3))) : d;
        }

        static string DayWord(DateTime d)
        {
            if (d == DateTime.Today) return "today";
            if (d == DateTime.Today.AddDays(1)) return "tomorrow";
            return DayNames[(int)d.DayOfWeek] + (d > DateTime.Today.AddDays(6) ? " " + d.ToString("MMM d", CultureInfo.InvariantCulture).ToLowerInvariant() : "");
        }

        static string ListLine(List<PlanItem> items)
        {
            return string.Join(", ", items.Select((p, i) => (i + 1) + ". " + p.Text + (p.Time.Length > 0 ? " (" + When.Clock(At(p.Day, p.Time)) + ")" : "") + (p.Done ? " (done)" : "")).ToArray());
        }

        // ------------------------------------------------------------------ changing it

        /// <summary>Puts these tasks on their days (replacing what wasn't done yet on those days) and sets each day's reminder.
        /// lines: "day | task | time | why", in order.</summary>
        public static string Make(string lines, string start, AssistantReminders reminders)
        {
            var parsed = new List<PlanItem>();
            var problems = new List<string>();
            foreach (string raw in (lines ?? "").Split('\n'))
            {
                string[] f = raw.Split('|').Select(x => x.Trim()).ToArray();
                if (f.Length < 2 || f[1].Length == 0) { if (raw.Trim().Length > 0) problems.Add(raw.Trim()); continue; }
                DateTime? day = ParseDay(f[0]);
                if (day == null || day.Value < DateTime.Today) { problems.Add(raw.Trim()); continue; }
                parsed.Add(new PlanItem { Text = f[1].TrimEnd('.'), Day = day.Value.ToString("yyyy-MM-dd"), Time = f.Length > 2 ? ParseTime(f[2]) : "", Why = f.Length > 3 ? f[3] : "" });
            }
            if (parsed.Count == 0) return "nothing to plan" + (problems.Count > 0 ? " (couldn't read: " + string.Join(" / ", problems.ToArray()) + "; use 'day | task | time | why' lines)" : "") + ".";
            string defaultStart = ParseTime(start);
            var report = new StringBuilder();
            lock (gate)
            {
                List<PlanItem> all = Load();
                var days = new HashSet<string>(parsed.Select(p => p.Day));
                foreach (PlanItem old in all.Where(p => days.Contains(p.Day)).ToList())
                {
                    CancelReminder(reminders, old.ReminderId);   // the day's reminder is made again below, with the new list
                    CancelReminder(reminders, old.NudgeId);
                    old.ReminderId = old.NudgeId = "";
                    if (!old.Done) all.Remove(old);
                }
                int id = all.Count == 0 ? 1 : all.Max(p => { int n; return int.TryParse(p.Id.TrimStart('p'), out n) ? n : 0; }) + 1;
                foreach (var g in parsed.GroupBy(p => p.Day).OrderBy(g => g.Key))
                {
                    int order = all.Where(p => p.Day == g.Key).Select(p => p.Order).DefaultIfEmpty(0).Max();
                    foreach (PlanItem p in g) { p.Id = "p" + (id++); p.Order = ++order; all.Add(p); }
                    List<PlanItem> dayItems = all.Where(p => p.Day == g.Key).OrderBy(p => p.Order).ToList();
                    DateTime date = DateTime.ParseExact(g.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    // the day's list pops up at the start time (theirs, the first task's, or 10 am / 4 pm on school days)
                    string firstTime = g.Where(p => p.Time.Length > 0).Select(p => p.Time).OrderBy(t => t).FirstOrDefault();
                    bool weekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                    string startAt = defaultStart.Length > 0 ? defaultStart : firstTime ?? (weekend ? "10:00" : "16:00");
                    DateTime due = At(g.Key, startAt);
                    if (firstTime != null && At(g.Key, firstTime) < due) due = At(g.Key, firstTime);
                    if (due < DateTime.Now.AddMinutes(5)) due = date == DateTime.Today ? DateTime.Now.AddMinutes(30) : due;
                    List<PlanItem> open = dayItems.Where(p => !p.Done).ToList();
                    if (due > DateTime.Now && open.Count > 0)
                    {
                        AssistantReminder r = reminders.Add(due.ToUniversalTime(), DayReminderText(open));
                        foreach (PlanItem p in dayItems) p.ReminderId = r.Id;
                        // a task with its own time later than that gets its own nudge too
                        foreach (PlanItem p in open.Where(p => p.Time.Length > 0 && At(p.Day, p.Time) > due.AddMinutes(10)))
                            p.NudgeId = reminders.Add(At(p.Day, p.Time).ToUniversalTime(), "time for: " + p.Text).Id;
                    }
                    report.Append(DayWord(date)).Append(": ").Append(ListLine(dayItems))
                          .Append(due > DateTime.Now ? " (pops up " + When.Describe(due, DateTime.Now) + ")" : "").Append(". ");
                }
                Save(all);
            }
            Journal.Add("i made a plan with you: " + report.ToString().Trim());
            return "planned. " + report.ToString().Trim() + (problems.Count > 0 ? " couldn't read: " + string.Join(" / ", problems.ToArray()) + "." : "");
        }

        static string DayReminderText(List<PlanItem> open)
        {
            if (open.Count == 1) return "today's plan: " + open[0].Text + ".";
            return "today's plan, in order: " + string.Join("; then ", open.Select(p => p.Text).ToArray()) + ". start with " + open[0].Text + ".";
        }

        static void CancelReminder(AssistantReminders reminders, string id)
        {
            if (string.IsNullOrEmpty(id) || reminders == null) return;
            int n;
            try { reminders.Cancel(id, out n); } catch { }
        }

        /// <summary>Checks off the task matching this text (today's first, then the soonest), and says what's next.</summary>
        public static string Done(string match, bool undo, AssistantReminders reminders)
        {
            match = (match ?? "").Trim();
            if (match.Length == 0) return "which one?";
            lock (gate)
            {
                List<PlanItem> all = Load();
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                var hits = all.Where(p => p.Done == undo && p.Text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(p => p.Day == today ? 0 : 1).ThenBy(p => p.Day).ThenBy(p => p.Order).ToList();
                if (hits.Count == 0)
                {
                    // the words they used may not be the ones written down: try any longer word
                    string[] words = match.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).ToArray();
                    hits = all.Where(p => p.Done == undo && words.Length > 0 && words.Any(w => p.Text.ToLowerInvariant().Contains(w)))
                        .OrderBy(p => p.Day == today ? 0 : 1).ThenBy(p => p.Day).ThenBy(p => p.Order).ToList();
                }
                if (hits.Count == 0)
                {
                    var open = all.Where(p => !p.Done && string.CompareOrdinal(p.Day, today) >= 0).Select(p => p.Text).ToArray();
                    return "no planned task like \"" + match + "\"" + (open.Length > 0 ? ". what's planned: " + string.Join(", ", open) : "") + ".";
                }
                PlanItem hit = hits[0];
                hit.Done = !undo;
                if (!undo) { CancelReminder(reminders, hit.NudgeId); hit.NudgeId = ""; }
                // the day's whole list is done: its reminder can go
                if (!undo && all.Where(p => p.Day == hit.Day).All(p => p.Done)) CancelReminder(reminders, hit.ReminderId);
                Save(all);
                if (undo) return "unchecked: " + hit.Text + ".";
                Journal.Add("you finished: " + hit.Text);
                PlanItem next = all.Where(p => !p.Done && string.CompareOrdinal(p.Day, today) >= 0).OrderBy(p => p.Day).ThenBy(p => p.Order).FirstOrDefault();
                int leftToday = all.Count(p => !p.Done && p.Day == today);
                return "checked off: " + hit.Text + ". " + (next == null ? "that's everything on the plan." : "next: " + next.Text
                    + (next.Day == today ? "" : " (" + DayWord(DateTime.ParseExact(next.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)) + ")") + ". " + leftToday + " left today.");
            }
        }

        public static string Clear(string day, AssistantReminders reminders)
        {
            lock (gate)
            {
                List<PlanItem> all = Load();
                DateTime? d = string.IsNullOrWhiteSpace(day) ? (DateTime?)null : ParseDay(day);
                string today = DateTime.Today.ToString("yyyy-MM-dd");
                var gone = all.Where(p => d.HasValue ? p.Day == d.Value.ToString("yyyy-MM-dd") : string.CompareOrdinal(p.Day, today) >= 0).ToList();
                foreach (PlanItem p in gone) { CancelReminder(reminders, p.ReminderId); CancelReminder(reminders, p.NudgeId); all.Remove(p); }
                Save(all);
                return gone.Count == 0 ? "nothing planned there." : "cleared " + gone.Count + " planned task" + (gone.Count == 1 ? "" : "s") + ".";
            }
        }

        public static string Show()
        {
            var days = Upcoming();
            if (days.Count == 0) return "nothing planned.";
            return string.Join(" ", days.Select(d => DayWord(d.Key) + ": " + ListLine(d.Value) + ".").ToArray());
        }

        /// <summary>For the live context: what's planned today and next, or null.</summary>
        public static string ContextLine()
        {
            var days = Upcoming().Take(3).ToList();
            if (days.Count == 0) return null;
            return "their plan (plan tool; check things off with done when they finish one, and when they ask what to do, it's the first unchecked one): "
                + string.Join(" ", days.Select(d => DayWord(d.Key) + ": " + ListLine(d.Value) + ".").ToArray());
        }
    }

    internal sealed partial class AssistantTools
    {
        void AddPlanTools(List<object> t)
        {
            t.Add(Tool("plan", "their plan for the next few days. make: when they list things they want to get done over some days ('this weekend i want to "
                + "do x, y and z', 'before friday i need to...'), YOU decide the order and the day for each: deadlines and things others wait on first, big or "
                + "hard ones early in the day and early in the window, quick wins in between, fun last; spread them so no day is overloaded. each day's list "
                + "pops up for them as a reminder that day, so then tell them the plan in a sentence or two (what's first each day and why). making a plan "
                + "replaces what wasn't done yet on those days, so pass the whole plan for each day. done: they finished one ('i did x') - it says what's next. "
                + "undo: uncheck one. show: what's planned. clear: drop a day (or everything ahead).", R("action"),
                PE("action", "what to do", new[] { "make", "done", "undo", "show", "clear" }),
                P("tasks", "string", "make: one task per line, in the order they should do them: 'day | task | time | why'. day: today, tomorrow, saturday, "
                    + "or 2026-10-11. time is optional (only if they gave one, e.g. '4 pm'); why is a few words, e.g. 'due monday'"),
                P("start", "string", "make: when each day's list should pop up, if they said (e.g. '9 am'); default 10 am on weekends, 4 pm on school days"),
                P("task", "string", "done / undo: the task, in their words"),
                P("day", "string", "clear: which day (empty: everything ahead)")));
        }

        AssistantToolResult PlanTool(string name, Dictionary<string, object> a)
        {
            if (name != "plan") return null;
            string action = Json.Str(a, "action") ?? "show";
            if (DryRun) return new AssistantToolResult("done.");
            AssistantReminders reminders = module != null ? module.Reminders : null;
            switch (action)
            {
                case "make":
                    if (reminders == null) return new AssistantToolResult("reminders aren't running, so i can't plan right now.");
                    return new AssistantToolResult(Plans.Make(Json.Str(a, "tasks"), Json.Str(a, "start"), reminders) + " tell them the plan briefly.", "plan", null);
                case "done": return new AssistantToolResult(Plans.Done(Json.Str(a, "task"), false, reminders), "checked off", null);
                case "undo": return new AssistantToolResult(Plans.Done(Json.Str(a, "task"), true, reminders));
                case "clear": return new AssistantToolResult(Plans.Clear(Json.Str(a, "day"), reminders), "plan cleared", null);
                default: return new AssistantToolResult(Plans.Show());
            }
        }
    }
}
