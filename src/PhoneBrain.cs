// PhoneBrain.cs - the data behind the second brain (phone\brain.html, at /brain on the computer and the phone): everything
// it remembers about them as a list of facts, plus today (calendar, reminders, weather), what's playing, their claude
// sessions and its own journal. Read-only. Slow things (weather, the calendar) come from a copy kept fresh in the
// background, so the page never waits on them. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bestie
{
    internal sealed partial class PhoneServer
    {
        /// <summary>The second brain on this computer, already paired (the page keeps the key and drops it from the address).</summary>
        public string LocalBrainUrl { get { return "http://127.0.0.1:" + Port + "/brain?k=" + Key; } }
        /// <summary>The second brain on the phone (paired already if they use the phone app), or null until tailscale's name is known.</summary>
        public string PhoneBrainUrl { get { return tailnetHost == null ? null : "https://" + tailnetHost + ":" + TailscalePort + "/brain"; } }

        static readonly object brainGate = new object();
        static WeatherReport brainWx;
        static List<CalEvent> brainCal;
        static DateTime brainWxAt = DateTime.MinValue, brainCalAt = DateTime.MinValue, brainCalDay;
        static bool brainWxBusy, brainCalBusy;
        static readonly Regex FactDate = new Regex(@"\s*_\((\d{4}-\d{2}-\d{2})\)_\s*$"), InlineDate = new Regex(@"\s*\(\d{4}-\d{2}-\d{2}\)\s*$");

        Dictionary<string, object> BrainJson()
        {
            Freshen();
            // the first look of the day: give the weather and calendar a moment to arrive
            for (int i = 0; i < 25; i++)
            {
                lock (brainGate) if (!(brainWx == null && brainWxBusy) && !(brainCal == null && brainCalBusy)) break;
                Thread.Sleep(100);
            }
            var c = CreatureInfo();
            List<object> mems = Memories();
            var d = new Dictionary<string, object>();
            d["name"] = Json.Str(c, "name") ?? "Bestie";
            d["memories"] = mems;
            d["counts"] = new Dictionary<string, object> { { "memories", mems.Count }, { "journalDays", CountFiles(Home.JournalDir, "*.md", false) },
                { "notes", CountFiles(Home.NotesDir, "*.*", true) } };
            WeatherReport w; List<CalEvent> cal;
            lock (brainGate) { w = brainWx; cal = brainCal; }
            if (w != null)
                d["weather"] = new Dictionary<string, object> { { "tempF", w.TempF }, { "sky", w.Sky }, { "highF", w.HighF }, { "lowF", w.LowF },
                    { "alerts", w.Alerts.Select(a => (object)new Dictionary<string, object> { { "event", a.Event }, { "until", When.Clock(a.Expires) }, { "area", a.Area } }).ToList() } };
            d["agenda"] = Agenda(cal);
            try { Song song = Music.Detect(); if (song != null) d["music"] = new Dictionary<string, object> { { "title", song.Title }, { "artist", song.Artist } }; } catch { }
            try { d["radio"] = assistant != null && assistant.Radio.On; } catch { }
            d["sessions"] = Sessions();
            d["journal"] = JournalLines(6);
            return d;
        }

        /// <summary>Starts a background refresh of the weather and today's calendar when they're getting old.</summary>
        void Freshen()
        {
            AssistantModule a = assistant;
            lock (brainGate)
            {
                if (!brainWxBusy && DateTime.Now - brainWxAt > TimeSpan.FromMinutes(4))
                {
                    brainWxBusy = true;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        WeatherReport w = null;
                        try { w = Weather.Now(); } catch (Exception ex) { DebugLog.Write("brain: weather: " + ex.Message); }
                        lock (brainGate) { if (w != null) brainWx = w; brainWxAt = DateTime.Now; brainWxBusy = false; }
                    });
                }
                if (!brainCalBusy && (DateTime.Now - brainCalAt > TimeSpan.FromMinutes(5) || brainCalDay != DateTime.Today))
                {
                    brainCalBusy = true;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        List<CalEvent> evs = null;
                        DateTime day = DateTime.Today;
                        try
                        {
                            AssistantCalendar g = a != null ? a.Calendar : null;
                            if (g != null && g.IsConnected) evs = g.ListEvents("primary", day, day.AddDays(1), null, 30, 15000);
                            else if (IcsCalendar.Configured) evs = IcsCalendar.Events(day, day.AddDays(1));
                        }
                        catch (Exception ex) { DebugLog.Write("brain: calendar: " + ex.Message); }
                        lock (brainGate)
                        {
                            if (evs != null || brainCalDay != day) brainCal = evs ?? new List<CalEvent>();
                            // nothing yet often means the calendar was still being read in the browser: look again in a minute
                            brainCalAt = evs != null && evs.Count > 0 ? DateTime.Now : DateTime.Now.AddMinutes(-4);
                            brainCalDay = day; brainCalBusy = false;
                        }
                    });
                }
            }
        }

        static string ReadShared(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(f, Encoding.UTF8)) return r.ReadToEnd();
            }
            catch { return ""; }
        }

        /// <summary>about-you.md as facts: "- likes bowie _(2026-09-23)_" -> {text, date}.</summary>
        static List<object> Memories()
        {
            var list = new List<object>();
            foreach (string raw in ReadShared(Path.Combine(Home.MemoryDir, "about-you.md")).Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("- ")) continue;
                string text = line.Substring(2).Trim(), date = "";
                Match m = FactDate.Match(text);
                if (m.Success) { date = m.Groups[1].Value; text = text.Substring(0, m.Index); }
                text = InlineDate.Replace(text, "").Trim();
                if (text.Length > 0) list.Add(new Dictionary<string, object> { { "text", text }, { "date", date } });
            }
            return list;
        }

        static int CountFiles(string dir, string pattern, bool deep)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern, deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Length : 0; }
            catch { return 0; }
        }

        /// <summary>Today: calendar events (all-day first) and the reminders still due today, in time order.</summary>
        List<object> Agenda(List<CalEvent> cal)
        {
            DateTime today = DateTime.Today, tomorrow = today.AddDays(1);
            var items = new List<KeyValuePair<DateTime, Dictionary<string, object>>>();
            foreach (CalEvent e in cal ?? new List<CalEvent>())
            {
                if (e.Start >= tomorrow || (e.End <= today && e.Start < today)) continue;
                items.Add(new KeyValuePair<DateTime, Dictionary<string, object>>(e.AllDay ? today.AddSeconds(-1) : e.Start, new Dictionary<string, object> {
                    { "at", e.Start.ToString("s") }, { "label", e.AllDay ? "all day" : When.Clock(e.Start) }, { "title", e.Title ?? "" }, { "kind", "event" }, { "allDay", e.AllDay } }));
            }
            try
            {
                if (assistant != null)
                    foreach (AssistantReminder r in assistant.PendingReminders())
                    {
                        DateTime due = r.DueUtc.ToLocalTime();
                        if (due.Date != today) continue;
                        string repeat = "";
                        if (!string.IsNullOrEmpty(r.Repeat)) try { repeat = When.DescribeRepeat(r.Repeat); } catch { }
                        items.Add(new KeyValuePair<DateTime, Dictionary<string, object>>(due, new Dictionary<string, object> {
                            { "at", due.ToString("s") }, { "label", When.Clock(due) }, { "title", r.Text ?? "" }, { "kind", "reminder" }, { "repeat", repeat } }));
                    }
            }
            catch { }
            return items.OrderBy(x => x.Key).Select(x => (object)x.Value).ToList();
        }

        static List<object> Sessions()
        {
            try
            {
                return AssistantTools.TheirSessions().Take(6).Select(x => (object)new Dictionary<string, object> {
                    { "title", x.Title ?? "" }, { "project", x.Where ?? "" }, { "state", x.Status ?? "" }, { "stateLabel", AiSessions.StatusWords(x.Status) },
                    { "ago", AiSessions.Ago(x.LastActive).Replace(" ago", "") } }).ToList();
            }
            catch { return new List<object>(); }
        }

        /// <summary>Its newest journal lines ("- 14:05 — ..."), today's or else yesterday's, newest first.</summary>
        static List<object> JournalLines(int max)
        {
            foreach (DateTime day in new[] { DateTime.Today, DateTime.Today.AddDays(-1) })
            {
                var lines = ReadShared(Journal.PathFor(day)).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ")).ToList();
                if (lines.Count == 0) continue;
                var list = new List<object>();
                for (int i = lines.Count - 1; i >= 0 && list.Count < max; i--)
                {
                    Match m = Regex.Match(lines[i], @"^- (\d{1,2}):(\d{2}) — (.+)$");
                    if (!m.Success) continue;
                    var t = day.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value));
                    list.Add(new Dictionary<string, object> { { "time", When.Clock(t) }, { "text", m.Groups[3].Value }, { "yesterday", day != DateTime.Today } });
                }
                return list;
            }
            return new List<object>();
        }
    }

    internal sealed partial class AssistantTools
    {
        void AddSecondBrainTools(List<object> t)
        {
            t.Add(Tool("second_brain", "open their second brain on the computer: a dashboard with a map of everything you remember about them (grouped by topic, "
                + "linked by shared people, songs and projects), today's calendar and reminders, weather alerts, what's playing, their claude sessions and your "
                + "journal, with a box to ask you things. also gives the link for their phone.", R()));
        }

        AssistantToolResult SecondBrainTool(string name, Dictionary<string, object> a)
        {
            if (name != "second_brain") return null;
            PhoneServer p = PhoneServer.Current;
            if (p == null) return new AssistantToolResult("the second brain lives on the phone server, and it didn't start this time.");
            if (DryRun) return new AssistantToolResult("done.");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p.LocalBrainUrl) { UseShellExecute = true }); }
            catch (Exception ex) { return new AssistantToolResult("couldn't open it: " + ex.Message); }
            string phone = p.PhoneBrainUrl;
            return new AssistantToolResult("opened the second brain on the computer." + (phone != null ? " on their phone it's " + phone + " (or the brain button in the phone app)." : "")
                + " say it in a few words.", "second brain", null);
        }
    }
}
