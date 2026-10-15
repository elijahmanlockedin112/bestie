// PatternMiner.cs - the deeper patterns in how they work, looked for every night in the behavior log (behavior\*.jsonl),
// what they ask Bestie for and when, its journal, and the focus blocks: the hours they drift and to what, how long their
// focus lasts and what ends it, what sets off a detour, what they go back to, what they ask at the same time every day.
// The numbers are worked out here; the model only turns them into a few plain sentences, and has to cite them.
// Kept in .state\patterns.json, and memory\patterns.md so they're in every prompt. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bestie
{
    internal sealed class Pattern
    {
        public string Key = "", Kind = "", Text = "", Evidence = "", When = "", Tip = "", First = "", Last = "";
        public int Strength, Seen;
    }

    internal static class PatternMiner
    {
        const int WindowDays = 28;
        static readonly HashSet<string> Drift = new HashSet<string> { "video", "games", "social" };
        static readonly HashSet<string> NoiseTools = new HashSet<string> { "remember", "forget", "my_patterns", "patterns", "look_at_screen" };
        static int running;
        static DateTime nextTry = DateTime.MinValue;
        static string StatePath { get { return Home.State("patterns.json"); } }
        public static string MemoryPath { get { Home.Init(); return Path.Combine(Home.MemoryDir, "patterns.md"); } }

        // ================================================================== reading what was saved

        public static List<Pattern> Saved(out DateTime updated)
        {
            updated = DateTime.MinValue;
            var list = new List<Pattern>();
            try
            {
                if (!File.Exists(StatePath)) return list;
                var d = Json.Obj(File.ReadAllText(StatePath, Encoding.UTF8));
                DateTime.TryParse(Json.Str(d, "updated") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out updated);
                foreach (object o in Json.Arr(d, "patterns"))
                {
                    var p = o as Dictionary<string, object>;
                    if (p == null) continue;
                    list.Add(new Pattern { Key = Json.Str(p, "key") ?? "", Kind = Json.Str(p, "kind") ?? "", Text = Json.Str(p, "text") ?? "", Evidence = Json.Str(p, "evidence") ?? "",
                        When = Json.Str(p, "when") ?? "", Tip = Json.Str(p, "tip") ?? "", First = Json.Str(p, "first") ?? "", Last = Json.Str(p, "last") ?? "",
                        Strength = (int)Json.Num(p, "strength", 1), Seen = (int)Json.Num(p, "seen", 1) });
                }
            }
            catch (Exception ex) { DebugLog.Write("patterns: " + ex.Message); }
            return list;
        }

        /// <summary>The time-bound patterns that are about now (within an hour), for the live context; or null.</summary>
        public static string NowLine()
        {
            DateTime updated;
            var now = Saved(out updated).Where(p => IsNow(p.When, DateTime.Now)).ToList();
            if (now.Count == 0) return null;
            return "around this time of day they usually (from their patterns; use it if it helps, don't nag): " + string.Join(" / ", now.Select(p => p.Text).ToArray());
        }

        internal static bool IsNow(string when, DateTime at)
        {
            TimeSpan t;
            if (string.IsNullOrEmpty(when) || !TimeSpan.TryParse(when, CultureInfo.InvariantCulture, out t)) return false;
            double diff = Math.Abs((at.TimeOfDay - t).TotalMinutes);
            return Math.Min(diff, 1440 - diff) <= 60;
        }

        // ================================================================== when to look

        /// <summary>From the memory timer (every 20 s): once a day, in the evening (or right away if it's been a day and a half).</summary>
        public static void MaybeRun(AssistantSettings s)
        {
            if (s == null || !s.HasKey || !s.AutoMemory || DateTime.Now < nextTry) return;
            DateTime updated;
            Saved(out updated);
            DateTime now = DateTime.Now;
            if (updated.Date >= now.Date) return;
            if (now.Hour < 21 && (now - updated).TotalHours < 36) return;
            nextTry = now.AddMinutes(30);
            Run(s);
        }

        // ================================================================== the numbers

        sealed class Session { public DateTime From, To; public string Kind = "", EndedBy = ""; public bool Music; public double Minutes { get { return (To - From).TotalMinutes; } } }

        static string Label(Behavior.Span s)
        {
            if (!string.IsNullOrEmpty(s.Site)) return s.Site.StartsWith("www.") ? s.Site.Substring(4) : s.Site;
            return string.IsNullOrEmpty(s.App) ? s.Cat : s.App;
        }

        static string Hour(int h) { return h == 0 ? "12 am" : h < 12 ? h + " am" : h == 12 ? "12 pm" : (h - 12) + " pm"; }
        static string Clock(DateTime t) { return When.Clock(t); }
        static void Add(Dictionary<string, double> d, string k, double v) { double x; d.TryGetValue(k, out x); d[k] = x + v; }
        static string Top(Dictionary<string, double> d, int n, Func<double, string> fmt)
        {
            return string.Join(", ", d.Where(x => x.Value >= 1).OrderByDescending(x => x.Value).Take(n).Select(x => x.Key + " " + fmt(x.Value)).ToArray());
        }
        static double Median(List<double> xs) { if (xs.Count == 0) return 0; var s = xs.OrderBy(x => x).ToList(); return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2; }
        static DateTime EventAt(Behavior.Day d, Dictionary<string, object> e)
        {
            TimeSpan t;
            return TimeSpan.TryParse(Json.Str(e, "at") ?? "", CultureInfo.InvariantCulture, out t) ? d.Date + t : DateTime.MinValue;
        }

        /// <summary>Everything worth knowing from the last few weeks, as plain lines with numbers.</summary>
        public static string Facts(int window, out int daysWithData)
        {
            var days = new List<Behavior.Day>();
            for (int i = window - 1; i >= 0; i--)
            {
                Behavior.Day d = Behavior.Load(DateTime.Today.AddDays(-i));
                if (d.Uses.Sum(u => u.Secs) >= 1800) days.Add(d);   // a day with at least half an hour at the computer
            }
            daysWithData = days.Count;
            var sb = new StringBuilder();
            if (days.Count == 0) return "";
            sb.Append("DATA: ").Append(days.Count).Append(" days with activity, ").Append(days[0].Date.ToString("ddd MMM d", CultureInfo.InvariantCulture))
              .Append(" to ").Append(days[days.Count - 1].Date.ToString("ddd MMM d", CultureInfo.InvariantCulture)).Append(". categories: video/games/social = drifting; coding/ai/school = work.\n");
            Try(sb, "hours", () => HourFacts(days, sb));
            List<Session> sessions = null;
            Try(sb, "focus", () => sessions = SessionFacts(days, sb));
            Try(sb, "drift", () => DriftFacts(days, sb));
            Try(sb, "comebacks", () => ComebackFacts(days, sb));
            Try(sb, "asks", () => AskFacts(days, sb));
            Try(sb, "days", () => DayFacts(days, sb));
            Try(sb, "blocks", () => BlockFacts(days, sb));
            return sb.ToString();
        }

        static void Try(StringBuilder sb, string what, Action a)
        {
            try { a(); }
            catch (Exception ex) { DebugLog.Write("patterns: " + what + ": " + ex.Message); }
        }

        /// <summary>Hour by hour: when they drift, when they work, when they're scattered.</summary>
        static void HourFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var present = new int[24]; var driftDays = new int[24]; var workDays = new int[24];
            var driftMin = new double[24]; var workMin = new double[24];
            var switches = new List<double>[24]; var driftWhat = new Dictionary<string, double>[24];
            for (int h = 0; h < 24; h++) { switches[h] = new List<double>(); driftWhat[h] = new Dictionary<string, double>(); }
            foreach (Behavior.Day d in days)
            {
                var at = new double[24]; var dr = new double[24]; var wk = new double[24]; var sw = new int[24];
                var what = new Dictionary<string, double>[24];
                for (int h = 0; h < 24; h++) what[h] = new Dictionary<string, double>();
                foreach (Behavior.Span s in d.Uses)
                {
                    if (s.From.Date == d.Date) sw[s.From.Hour]++;
                    for (DateTime t = s.From; t < s.To && t.Date == d.Date; )
                    {
                        DateTime next = t.Date.AddHours(t.Hour + 1);
                        if (next > s.To) next = s.To;
                        double secs = (next - t).TotalSeconds;
                        at[t.Hour] += secs;
                        if (Drift.Contains(s.Cat)) { dr[t.Hour] += secs; Add(what[t.Hour], Label(s), secs); }
                        if (Behavior.Productive.Contains(s.Cat)) wk[t.Hour] += secs;
                        t = next;
                    }
                }
                for (int h = 0; h < 24; h++)
                {
                    if (at[h] < 600) continue;
                    present[h]++; switches[h].Add(sw[h] * 3600.0 / at[h]);
                    if (dr[h] >= 480 && dr[h] >= at[h] * 0.4) { driftDays[h]++; driftMin[h] += dr[h] / 60; foreach (var kv in what[h]) Add(driftWhat[h], kv.Key, kv.Value); }
                    if (wk[h] >= 900 && wk[h] >= at[h] * 0.5) { workDays[h]++; workMin[h] += wk[h] / 60; }
                }
            }
            for (int h = 0; h < 24; h++)
                if (present[h] >= 2 && driftDays[h] >= 2 && driftDays[h] * 2 >= present[h])
                    sb.Append("drift hour ").Append(Hour(h)).Append(": mostly drifting on ").Append(driftDays[h]).Append(" of ").Append(present[h])
                      .Append(" days they were on then (avg ").Append(Math.Round(driftMin[h] / driftDays[h])).Append(" min of the hour); on: ")
                      .Append(Top(driftWhat[h], 3, v => Math.Round(v / 60) + " min")).Append('\n');
            for (int h = 0; h < 24; h++)
                if (present[h] >= 2 && workDays[h] >= 2 && workDays[h] * 2 >= present[h])
                    sb.Append("work hour ").Append(Hour(h)).Append(": mostly working on ").Append(workDays[h]).Append(" of ").Append(present[h])
                      .Append(" days (avg ").Append(Math.Round(workMin[h] / workDays[h])).Append(" min)\n");
            var all = switches.SelectMany(x => x).ToList();
            double typical = Median(all);
            var scattered = Enumerable.Range(0, 24).Where(h => switches[h].Count >= 2 && Median(switches[h]) >= typical * 1.6 && Median(switches[h]) >= 30)
                .OrderByDescending(h => Median(switches[h])).Take(2).ToList();
            foreach (int h in scattered)
                sb.Append("scattered hour ").Append(Hour(h)).Append(": ~").Append(Math.Round(Median(switches[h]))).Append(" window switches an hour vs ~")
                  .Append(Math.Round(typical)).Append(" usually (").Append(switches[h].Count).Append(" days)\n");
        }

        /// <summary>Stretches of work (8+ min, detours under 2 min allowed): how long they last, what ends them, music or not.</summary>
        static List<Session> SessionFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var all = new List<Session>();
            foreach (Behavior.Day d in days)
            {
                Session cur = null; var mix = new Dictionary<string, double>();
                foreach (Behavior.Span s in d.Uses)
                {
                    if (!Behavior.Productive.Contains(s.Cat)) continue;
                    if (cur != null && (s.From - cur.To).TotalSeconds <= 120) { if (s.To > cur.To) cur.To = s.To; Add(mix, s.Cat, s.Secs); continue; }
                    Finish(cur, mix, d, all);
                    cur = new Session { From = s.From, To = s.To }; mix = new Dictionary<string, double>(); Add(mix, s.Cat, s.Secs);
                }
                Finish(cur, mix, d, all);
            }
            foreach (string kind in new[] { "studying", "building" })
            {
                var ss = all.Where(x => x.Kind == kind).ToList();
                if (ss.Count < 2) continue;
                var mins = ss.Select(x => x.Minutes).ToList();
                var ends = new Dictionary<string, double>();
                foreach (Session x in ss) Add(ends, x.EndedBy, 1);
                var starts = new Dictionary<string, double>();
                foreach (Session x in ss) Add(starts, Hour(x.From.Hour), 1);
                sb.Append(kind).Append(" sessions (").Append(kind == "studying" ? "school sites and docs" : "coding and AI tools").Append(", 8+ min): ").Append(ss.Count)
                  .Append(" over ").Append(ss.Select(x => x.From.Date).Distinct().Count()).Append(" days; lengths ")
                  .Append(string.Join(", ", mins.OrderBy(m => m).Select(m => Math.Round(m).ToString(CultureInfo.InvariantCulture)).ToArray())).Append(" min (median ")
                  .Append(Math.Round(Median(mins))).Append("); ended by: ").Append(Top(ends, 4, v => "x" + v)).Append("; usually start at: ").Append(Top(starts, 3, v => "x" + v)).Append('\n');
                var withMusic = ss.Where(x => x.Music).Select(x => x.Minutes).ToList();
                var without = ss.Where(x => !x.Music).Select(x => x.Minutes).ToList();
                if (withMusic.Count >= 2 && without.Count >= 2)
                    sb.Append(kind).Append(" with music on: median ").Append(Math.Round(Median(withMusic))).Append(" min (").Append(withMusic.Count)
                      .Append(" sessions) vs without: ").Append(Math.Round(Median(without))).Append(" min (").Append(without.Count).Append(")\n");
            }
            return all;
        }

        static void Finish(Session cur, Dictionary<string, double> mix, Behavior.Day d, List<Session> list)
        {
            if (cur == null || cur.Minutes < 8) return;
            cur.Kind = mix.OrderByDescending(x => x.Value).First().Key == "school" ? "studying" : "building";
            // what ended it: going away, or the first thing that wasn't work and lasted a minute
            Behavior.Span away = d.Aways.FirstOrDefault(a => a.From >= cur.To.AddSeconds(-30) && a.From <= cur.To.AddMinutes(3));
            if (away != null) cur.EndedBy = "went away";
            else
            {
                Behavior.Span next = d.Uses.FirstOrDefault(u => u.From >= cur.To.AddSeconds(-5) && !Behavior.Productive.Contains(u.Cat) && u.Secs >= 60);
                cur.EndedBy = next == null ? "end of the day" : Label(next) + " (" + next.Cat + ")";
            }
            cur.Music = d.Events.Any(e => Json.Str(e, "t") == "song" && EventAt(d, e) >= cur.From.AddMinutes(-15) && EventAt(d, e) <= cur.To);
            list.Add(cur);
        }

        /// <summary>Stretches of videos, games or social (5+ min): how long, when, what came right before.</summary>
        static void DriftFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var eps = new List<KeyValuePair<DateTime, double>>();
            var before = new Dictionary<string, double>(); var what = new Dictionary<string, double>(); var hours = new Dictionary<string, double>();
            int afterAgent = 0, agentDone = 0, agentThenDrift = 0;
            foreach (Behavior.Day d in days)
            {
                var agentTimes = AgentFinishes(d.Date);
                DateTime from = DateTime.MinValue, to = DateTime.MinValue; var label = new Dictionary<string, double>();
                Action close = () =>
                {
                    if (from == DateTime.MinValue || (to - from).TotalMinutes < 5) return;
                    eps.Add(new KeyValuePair<DateTime, double>(from, (to - from).TotalMinutes));
                    Add(hours, Hour(from.Hour), 1);
                    foreach (var kv in label) Add(what, kv.Key, kv.Value);
                    DateTime f = from;
                    Behavior.Span ret = d.Aways.FirstOrDefault(a => a.To <= f && a.To >= f.AddMinutes(-2) && a.Secs >= 300);
                    Behavior.Span prev = d.Uses.LastOrDefault(u => u.To <= f.AddSeconds(5) && !Drift.Contains(u.Cat) && u.Secs >= 30 && u.To >= f.AddMinutes(-5));
                    Add(before, ret != null ? "just came back to the computer" : prev != null ? Label(prev) + " (" + prev.Cat + ")" : "nothing in particular", 1);
                    if (agentTimes.Any(t => t <= f && t >= f.AddMinutes(-5))) afterAgent++;
                };
                foreach (Behavior.Span s in d.Uses)
                {
                    if (!Drift.Contains(s.Cat)) continue;
                    if (from != DateTime.MinValue && (s.From - to).TotalSeconds <= 120) { if (s.To > to) to = s.To; Add(label, Label(s), s.Secs / 60.0); continue; }
                    close();
                    from = s.From; to = s.To; label = new Dictionary<string, double>(); Add(label, Label(s), s.Secs / 60.0);
                }
                close();
                // claude/codex finishing while they were at the computer: what did they do next?
                foreach (DateTime t in agentTimes)
                {
                    bool here = d.Uses.Any(u => u.From <= t && u.To >= t.AddMinutes(-2));
                    if (!here) continue;
                    agentDone++;
                    if (d.Uses.Any(u => Drift.Contains(u.Cat) && u.From >= t && u.From <= t.AddMinutes(10) && u.Secs >= 120)) agentThenDrift++;
                }
            }
            if (eps.Count >= 2)
            {
                var mins = eps.Select(e => e.Value).ToList();
                sb.Append("drift stretches (5+ min): ").Append(eps.Count).Append(" over ").Append(eps.Select(e => e.Key.Date).Distinct().Count())
                  .Append(" days; median ").Append(Math.Round(Median(mins))).Append(" min, longest ").Append(Math.Round(mins.Max())).Append(" min; start at: ")
                  .Append(Top(hours, 4, v => "x" + v)).Append("; on: ").Append(Top(what, 4, v => Math.Round(v) + " min")).Append("; right before: ")
                  .Append(Top(before, 4, v => "x" + v)).Append('\n');
                if (afterAgent > 0) sb.Append(afterAgent).Append(" of those drift stretches started within 5 min of a claude/codex job finishing\n");
            }
            if (agentDone >= 3)
                sb.Append("claude/codex jobs that finished while they were at the computer: ").Append(agentDone).Append("; followed by videos/games/social within 10 min: ")
                  .Append(agentThenDrift).Append('\n');
        }

        /// <summary>When a claude or codex job finished, from the journal ("- 14:05 — claude finished ...").</summary>
        static List<DateTime> AgentFinishes(DateTime day)
        {
            var list = new List<DateTime>();
            try
            {
                string path = Journal.PathFor(day);
                if (!File.Exists(path)) return list;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    Match m = Regex.Match(line, @"^- (\d{1,2}):(\d{2}) — (claude|chatgpt|codex) finished");
                    if (m.Success) list.Add(day.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value)));
                }
            }
            catch { }
            return list;
        }

        /// <summary>After being away 10+ minutes, what they go to first.</summary>
        static void ComebackFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var first = new Dictionary<string, double>(); int n = 0;
            foreach (Behavior.Day d in days)
                foreach (Behavior.Span a in d.Aways.Where(x => x.Secs >= 600))
                {
                    Behavior.Span next = d.Uses.FirstOrDefault(u => u.From >= a.To.AddSeconds(-5) && u.Secs >= 60 && u.Cat != "bestie");
                    if (next == null) continue;
                    n++; Add(first, Label(next) + " (" + next.Cat + ")", 1);
                }
            if (n >= 3) sb.Append("coming back after 10+ min away (").Append(n).Append(" times), first thing: ").Append(Top(first, 4, v => "x" + v)).Append('\n');
        }

        /// <summary>What they ask Bestie for, and when (times from the behavior log, tool names from it and the debug log).</summary>
        static void AskFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var asks = new List<KeyValuePair<DateTime, string>>();   // time, what they said ("" before it was recorded)
            var tools = new List<KeyValuePair<DateTime, string>>();
            foreach (Behavior.Day d in days)
                foreach (var e in d.Events)
                {
                    string t = Json.Str(e, "t");
                    if (t == "ask") asks.Add(new KeyValuePair<DateTime, string>(EventAt(d, e), Json.Str(e, "what") ?? ""));
                    if (t == "tool") tools.Add(new KeyValuePair<DateTime, string>(EventAt(d, e), Json.Str(e, "name") ?? ""));
                }
            // tools from before the behavior log had them: the debug log's "assistant: tool x" lines
            DateTime firstLogged = tools.Count > 0 ? tools.Min(x => x.Key) : DateTime.MaxValue;
            foreach (string f in new[] { Home.State("debug.log.1"), Home.State("debug.log") })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    string[] lines;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, Encoding.UTF8)) lines = r.ReadToEnd().Split('\n');
                    foreach (string line in lines)
                    {
                        Match m = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) assistant: tool (\w+) -> ");
                        if (!m.Success) continue;
                        DateTime at = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                        if (at < firstLogged && at >= DateTime.Today.AddDays(-WindowDays)) tools.Add(new KeyValuePair<DateTime, string>(at, m.Groups[2].Value));
                    }
                }
                catch { }
            }
            if (asks.Count >= 3)
            {
                var byHour = new Dictionary<string, double>();
                foreach (var a in asks) Add(byHour, Hour(a.Key.Hour), 1);
                sb.Append("talking to bestie: ").Append(asks.Count).Append(" messages over ").Append(asks.Select(a => a.Key.Date).Distinct().Count())
                  .Append(" days; busiest hours: ").Append(Top(byHour, 3, v => "x" + v)).Append('\n');
            }
            // each kind of request: how many days, and whether it clusters at one time of day
            foreach (var g in tools.Where(x => !NoiseTools.Contains(x.Value)).GroupBy(x => x.Value).OrderByDescending(g => g.Count()))
            {
                int dayCount = g.Select(x => x.Key.Date).Distinct().Count();
                if (dayCount < 2) continue;
                sb.Append("asked bestie to ").Append(g.Key).Append(": ").Append(g.Count()).Append(" times on ").Append(dayCount).Append(" days").Append(Cluster(g.Select(x => x.Key).ToList())).Append('\n');
            }
            // the same thing said again and again (exact words, once they're recorded)
            foreach (var g in asks.Where(a => a.Value.Length > 0).GroupBy(a => Normalize(a.Value)).Where(g => g.Key.Length > 3 && g.Select(x => x.Key.Date).Distinct().Count() >= 2).OrderByDescending(g => g.Count()).Take(5))
                sb.Append("said \"").Append(g.First().Value).Append("\" ").Append(g.Count()).Append(" times on ").Append(g.Select(x => x.Key.Date).Distinct().Count())
                  .Append(" days").Append(Cluster(g.Select(x => x.Key).ToList())).Append('\n');
        }

        static string Normalize(string s)
        {
            s = Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9 ]", " ");
            s = Regex.Replace(s, @"^\s*(hey|hi|ok|okay|yo)?\s*(bestie|mothball|blorp)\b", " ");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        /// <summary>", mostly 12:30-2 pm (6 of 9)" when most of these times fall in one 90-minute window, else "".</summary>
        static string Cluster(List<DateTime> times)
        {
            if (times.Count < 3) return "";
            int best = 0, bestStart = 0;
            for (int start = 0; start < 1440; start += 15)
            {
                int n = times.Count(t => { double m = t.TimeOfDay.TotalMinutes - start; if (m < 0) m += 1440; return m < 90; });
                if (n > best) { best = n; bestStart = start; }
            }
            if (best < 3 || best * 10 < times.Count * 6) return "";
            DateTime a = DateTime.Today.AddMinutes(bestStart), b = a.AddMinutes(90);
            return ", mostly " + Clock(a) + "-" + Clock(b) + " (" + best + " of " + times.Count + ")";
        }

        /// <summary>When days start and end, late nights, weekdays vs weekends.</summary>
        static void DayFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var starts = new List<double>(); var ends = new List<double>(); int late = 0;
            var weekday = new List<double>(); var weekend = new List<double>();
            foreach (Behavior.Day d in days)
            {
                var morning = d.Uses.Where(u => u.From.Hour >= 5 && u.Secs >= 60).ToList();
                if (morning.Count > 0) starts.Add(morning.Min(u => u.From.TimeOfDay.TotalMinutes));
                if (d.Uses.Count > 0) ends.Add(d.Uses.Max(u => u.To.Date > d.Date ? 1440 : u.To.TimeOfDay.TotalMinutes));
                if (d.Uses.Any(u => u.To.TimeOfDay.TotalHours >= 23 || u.To.Date > d.Date)) late++;
                double work = d.Uses.Where(u => Behavior.Productive.Contains(u.Cat)).Sum(u => u.Secs) / 60.0;
                (d.Date.DayOfWeek == DayOfWeek.Saturday || d.Date.DayOfWeek == DayOfWeek.Sunday ? weekend : weekday).Add(work);
            }
            Func<double, string> at = m => Clock(DateTime.Today.AddMinutes(m));
            if (starts.Count >= 3) sb.Append("first on the computer: median ").Append(at(Median(starts))).Append(" (").Append(starts.Count).Append(" days)\n");
            if (ends.Count >= 3) sb.Append("last on the computer: median ").Append(at(Median(ends))).Append("; on past 11 pm on ").Append(late).Append(" of ").Append(days.Count).Append(" days\n");
            if (weekday.Count >= 2 && weekend.Count >= 2)
                sb.Append("work time a day: weekdays median ").Append(Math.Round(Median(weekday))).Append(" min, weekends ").Append(Math.Round(Median(weekend))).Append(" min\n");
        }

        static void BlockFacts(List<Behavior.Day> days, StringBuilder sb)
        {
            var counts = new Dictionary<string, double>();
            foreach (Behavior.Day d in days)
                foreach (var e in d.Events.Where(e => Json.Str(e, "t") == "block")) Add(counts, Json.Str(e, "what") ?? "?", 1);
            if (counts.Count > 0) sb.Append("focus blocks (90 min focus, 15 min break): ").Append(Top(counts, 6, v => "x" + v)).Append('\n');
        }

        // ================================================================== turning it into words

        const string Instructions =
"You find the real patterns in how one person spends time at their computer. They're a student who builds a lot of software with AI " +
"(Claude, Codex) and has classes to study for. You get numbers measured from their activity log: when they drift to videos, games and social media, " +
"how long their focus lasts and what ends it, what comes right before a detour, what they go back to, what they ask their desktop companion and when, " +
"and the shape of their days.\n" +
"Write the patterns a sharp, kind friend who watched for weeks would point out, the non-obvious ones first. Not only times of day: triggers " +
"(\"when a Claude job finishes you usually open YouTube\"), endurance (\"your study sessions end around 25 minutes\"), sequences, what helps " +
"(music, focus blocks), habits, and things they ask for at the same time.\n" +
"Rules:\n" +
"- Every pattern rests on the numbers given, and its evidence quotes them, numbers only, at most 12 words (\"4 of 5 days\", \"median 24 min, 7 sessions\"). Never invent.\n" +
"- A pattern needs 3+ occurrences. With only 2, include it only if it's striking, and call it early (strength 1).\n" +
"- text: second person, plain words, one short sentence, at most 18 words, no judging or lecturing. Real names (YouTube, Steam, Claude), never category " +
"codes or tool names (say \"ask me to play music\", not play_music).\n" +
"- tip: one practical idea that fits, at most 14 words, or \"\".\n" +
"- when: the time of day it centers on as HH:mm (24h) if it's about a time, else \"\".\n" +
"- kind: time, focus, trigger, habit, asks or day. strength: 1 early, 2 clear, 3 very consistent.\n" +
"- Keep continuity: if one of the PREVIOUS patterns still holds, reuse its key and update its wording and numbers; drop ones the numbers no longer back. " +
"key: short kebab-case.\n" +
"- 3 to 10 patterns, most useful first. If there's too little data for 3, give what there is.\n" +
"Reply with ONLY this JSON: {\"patterns\":[{\"key\":\"\",\"kind\":\"\",\"text\":\"\",\"evidence\":\"\",\"when\":\"\",\"strength\":2,\"tip\":\"\"}]}";

        /// <summary>Looks now: works out the numbers, asks the model for the patterns, saves them. Returns a short report.</summary>
        public static string Run(AssistantSettings s)
        {
            if (s == null || !s.HasKey) return "i need the openai key to look for patterns.";
            if (Interlocked.Exchange(ref running, 1) == 1) return "i'm already looking.";
            try
            {
                int days;
                string facts = Facts(WindowDays, out days);
                if (days < 2) return "i only have " + days + " day" + (days == 1 ? "" : "s") + " of your activity so far; patterns need a few more.";
                DateTime updated;
                List<Pattern> before = Saved(out updated);
                string known = "";
                try { if (File.Exists(AssistantMemory.AboutYouPath)) known = File.ReadAllText(AssistantMemory.AboutYouPath, Encoding.UTF8); } catch { }
                if (known.Length > 3000) known = known.Substring(known.Length - 3000);

                var body = new Dictionary<string, object>();
                body["model"] = s.BackgroundModel;
                body["messages"] = new List<object>
                {
                    new Dictionary<string, object> { { "role", "system" }, { "content", Instructions } },
                    new Dictionary<string, object> { { "role", "user" }, { "content",
                        "PREVIOUS PATTERNS:\n" + (before.Count == 0 ? "(none yet)" : string.Join("\n", before.Select(p => "- [" + p.Key + "] " + p.Text + " (" + p.Evidence + ")").ToArray()))
                        + "\n\nABOUT THEM (for context only):\n" + (known.Trim().Length > 0 ? known : "(nothing)")
                        + "\n\nNUMBERS (today is " + DateTime.Now.ToString("dddd MMM d, h:mm tt", CultureInfo.InvariantCulture) + "):\n" + facts } }
                };
                bool thinks = !string.IsNullOrEmpty(s.ReasoningEffort) && s.ReasoningEffort != "none";
                body["max_completion_tokens"] = thinks ? 4000 : 2000;
                if (!string.IsNullOrEmpty(s.ReasoningEffort)) body["reasoning_effort"] = s.ReasoningEffort;
                Dictionary<string, object> resp = new OpenAIClient(s.BaseUrl, s.ApiKey).ChatCompletion(body, 90000);
                object[] choices = Json.Arr(resp, "choices");
                var choice = choices.Length > 0 ? choices[0] as Dictionary<string, object> : null;
                string answer = Json.Str(Json.Obj(choice, "message"), "content") ?? "";
                List<Pattern> found = Parse(answer);
                if (found == null) { DebugLog.Write("patterns: the answer wasn't json"); return "i looked, but my notes came out garbled. try again in a bit."; }

                string today = DateTime.Today.ToString("yyyy-MM-dd");
                foreach (Pattern p in found)
                {
                    Pattern old = before.FirstOrDefault(b => b.Key == p.Key && p.Key.Length > 0);
                    p.First = old != null && old.First.Length > 0 ? old.First : today;
                    p.Seen = old != null ? old.Seen + 1 : 1;
                    p.Last = today;
                }
                Save(found, days);
                DebugLog.Write("patterns: found " + found.Count + " in " + days + " days");
                Journal.Add("i looked for patterns in your last " + days + " days and found " + found.Count + ".");
                return found.Count == 0 ? "nothing clear yet in " + days + " days."
                    : "from your last " + days + " days: " + string.Join(" ", found.Select(p => p.Text + " (" + p.Evidence + ")").ToArray());
            }
            catch (Exception ex)
            {
                DebugLog.Write("patterns: " + ex.Message);
                nextTry = DateTime.Now.AddMinutes(30);
                return "couldn't look just now (" + ex.Message + ").";
            }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        static List<Pattern> Parse(string text)
        {
            int a = (text ?? "").IndexOf('{'), b = (text ?? "").LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            Dictionary<string, object> d;
            try { d = Json.Obj(text.Substring(a, b - a + 1)); } catch { return null; }
            if (d == null) return null;
            var list = new List<Pattern>();
            foreach (object o in Json.Arr(d, "patterns"))
            {
                var p = o as Dictionary<string, object>;
                if (p == null) continue;
                var x = new Pattern { Key = Regex.Replace((Json.Str(p, "key") ?? "").ToLowerInvariant(), @"[^a-z0-9-]", ""), Kind = Json.Str(p, "kind") ?? "habit",
                    Text = AssistantMemory.OneLine(Json.Str(p, "text") ?? ""), Evidence = AssistantMemory.OneLine(Json.Str(p, "evidence") ?? ""),
                    When = Json.Str(p, "when") ?? "", Tip = AssistantMemory.OneLine(Json.Str(p, "tip") ?? ""), Strength = (int)Json.Num(p, "strength", 2) };
                TimeSpan t;
                if (x.When.Length > 0 && !TimeSpan.TryParse(x.When, CultureInfo.InvariantCulture, out t)) x.When = "";
                if (x.Text.Length >= 8) list.Add(x);
            }
            return list.Take(10).ToList();
        }

        static void Save(List<Pattern> patterns, int days)
        {
            var d = new Dictionary<string, object> {
                { "updated", DateTime.Now.ToString("s", CultureInfo.InvariantCulture) }, { "days", days },
                { "patterns", patterns.Select(p => (object)new Dictionary<string, object> { { "key", p.Key }, { "kind", p.Kind }, { "text", p.Text }, { "evidence", p.Evidence },
                    { "when", p.When }, { "tip", p.Tip }, { "strength", p.Strength }, { "first", p.First }, { "last", p.Last }, { "seen", p.Seen } }).ToList() } };
            File.WriteAllText(StatePath, Json.Write(d), new UTF8Encoding(false));
            // the memory folder copy: it goes into every conversation
            var md = new StringBuilder("# patterns in how you work\r\n_(found in your activity every night: when you drift, how long focus lasts, what you ask me. tell me if one's off.)_\r\n\r\n");
            foreach (Pattern p in patterns)
                md.Append("- ").Append(p.Text).Append(" _(").Append(p.Evidence).Append(")_").Append(p.Tip.Length > 0 ? " idea: " + p.Tip : "").Append("\r\n");
            File.WriteAllText(MemoryPath, md.ToString(), new UTF8Encoding(false));
        }
    }

    internal sealed partial class AssistantTools
    {
        void AddPatternTools(List<object> t)
        {
            t.Add(Tool("patterns", "the deeper patterns in how they work, found in their activity every night (when they drift and to what, how long focus "
                + "lasts and what ends it, what sets off a detour, what they ask you at the same time each day). show: what's been found (it's also in your "
                + "memory as patterns.md). refresh: look again now, over the last 4 weeks (takes ~20 s). for raw numbers on a period use my_patterns.", R("action"),
                PE("action", "what to do", new[] { "show", "refresh" })));
        }

        AssistantToolResult PatternTool(string name, Dictionary<string, object> a, AssistantSettings s)
        {
            if (name != "patterns") return null;
            if (DryRun) return new AssistantToolResult("done.");
            if (Json.Str(a, "action") == "refresh") return new AssistantToolResult(PatternMiner.Run(s) + " tell them the two or three most useful, briefly.", "patterns", null);
            DateTime updated;
            var list = PatternMiner.Saved(out updated);
            if (list.Count == 0) return new AssistantToolResult("no patterns found yet. refresh looks now.");
            return new AssistantToolResult("patterns (found " + updated.ToString("ddd h:mm tt", CultureInfo.InvariantCulture) + "): "
                + string.Join(" ", list.Select(p => p.Text + " (" + p.Evidence + ")" + (p.Tip.Length > 0 ? " idea: " + p.Tip : "")).ToArray()));
        }
    }
}
