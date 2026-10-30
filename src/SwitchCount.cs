// SwitchCount.cs - a live count of how often they switch off task: to another window or another tab in the browser (by
// the tab's id from the extension; without the extension, by the tab's title) that's video, social, games or chat, and
// isn't a video that's teaching them something. Moves that are part of the work don't count: editor to terminal to the
// app they're testing, homework to a search or ChatGPT, coming back to work, and any move to a window that was already
// up on another monitor. Fed by Behavior's 2-second look, so a window flashed past in under 2 seconds isn't counted.
// Bestie's own windows don't count, and nothing is counted while they're away. The day runs 4 am to 4 am, like the
// behavior log; today's count and the last hour of switch times live in .state\switches.json so a restart doesn't zero
// it. When they hop 15+ times in 5 minutes Bestie says so, once, then not again for 15 minutes (switchable off from the
// menu; never in quiet time or a break). It only talks: nothing is blocked or closed. C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Bestie
{
    internal static class SwitchCount
    {
        static readonly object gate = new object();
        static string day;                                    // yyyy-MM-dd of the 4-am day the count belongs to
        static int today;
        static readonly List<DateTime> recent = new List<DateTime>();   // switch times, last hour
        static string lastKey;
        static IntPtr lastWindow;
        static bool loaded;
        const int Rule = 2;   // bumped when what counts changes, so a day counted the old way starts over

        /// <summary>Where a switch lands that counts. Everything else (coding, ai, school, browsing, files, music, other)
        /// is part of the work.</summary>
        internal static readonly HashSet<string> OffTask = new HashSet<string> { "video", "social", "games", "chat" };
        static readonly System.Text.RegularExpressions.Regex Learning = new System.Text.RegularExpressions.Regex(
            @"\b(tutorial|how to|course|lesson|lecture|explained|learn|crash course|beginners?|walkthrough|cs50|python|javascript|typescript|c\+\+|c#|java|react|coding|programming|algebra|calculus|chemistry|biology|physics|history|ap )",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Was the window they left still up on another monitor (so this is a look across, not a switch)? Tests
        /// stand in for the real screens.</summary>
        internal static Func<IntPtr, IntPtr, bool> SideBySide = (from, to) =>
            from != IntPtr.Zero && to != IntPtr.Zero && from != to && Native.IsWindow(from) && Native.IsWindowVisible(from) && !Native.IsIconic(from)
            && Native.MonitorFromWindow(from, 2) != Native.MonitorFromWindow(to, 2);

        /// <summary>Does landing here take them off task? A video that's teaching them (a tutorial, a lecture) doesn't.</summary>
        internal static bool IsOffTask(string cat, string title)
        {
            if (cat == null || !OffTask.Contains(cat)) return false;
            return !(cat == "video" && Learning.IsMatch(title ?? ""));
        }

        // the warning: this many switches inside the window = they're tab-hopping. one callout, then a cooldown.
        internal const int WarnCount = 15;
        internal const double WarnWindowMinutes = 5, WarnCooldownMinutes = 15;
        static bool warn = true;
        static DateTime lastWarn = DateTime.MinValue;

        public static bool WarnEnabled { get { lock (gate) { Load(); return warn; } } }

        public static void SetWarn(bool on)
        {
            lock (gate) { Load(); warn = on; Save(); }
            DebugLog.Write("switches: warning " + (on ? "on" : "off"));
        }

        /// <summary>Switches in the last few minutes.</summary>
        public static int InLast(double minutes)
        {
            lock (gate)
            {
                Load();
                DateTime now = DateTime.Now;
                return recent.Count(t => (now - t).TotalMinutes < minutes);
            }
        }

        /// <summary>Call about once a second. The line to say when they've switched WarnCount+ times in the last
        /// WarnWindowMinutes, or null. Nothing while it's off, during quiet time or a focus-block break, or within the
        /// cooldown; switches from before the last warning don't count toward the next one. Any thread.</summary>
        public static string WarnTick() { return WarnTick(DateTime.Now, Quiet.On || (TimeBlocks.Running && TimeBlocks.Phase == "break")); }

        internal static string WarnTick(DateTime now, bool hush)
        {
            int n;
            lock (gate)
            {
                Load();
                if (!warn || hush || (now - lastWarn).TotalMinutes < WarnCooldownMinutes) return null;
                n = recent.Count(t => t > lastWarn && (now - t).TotalMinutes < WarnWindowMinutes && t <= now);
                if (n < WarnCount) return null;
                lastWarn = now;
            }
            DebugLog.Write("switches: warned at " + n + " in " + WarnWindowMinutes + " min");
            Journal.Add("you switched off task " + n + " times in " + WarnWindowMinutes + " minutes. told you to pick one.");
            return Pick(now,
                n + " switches in " + WarnWindowMinutes + " minutes. that's not working, that's channel surfing. pick one tab and stay.",
                "i'm writing you up. " + n + " tab switches in " + WarnWindowMinutes + " minutes. the tenants are getting dizzy. pick one thing.",
                "you've hopped " + n + " times in " + WarnWindowMinutes + " minutes. close what you don't need and give one thing ten minutes.",
                n + " switches. in " + WarnWindowMinutes + " minutes. i can't even count that fast and i count windows for fun. one tab. go.");
        }

        static string Pick(DateTime now, params string[] lines) { return lines[(int)(now.Ticks / TimeSpan.TicksPerSecond % lines.Length)]; }

        /// <summary>Raised (on Behavior's timer thread) when the count changes.</summary>
        public static event Action Changed;

        /// <summary>Tests point this at a scratch file so they never touch the real count.</summary>
        internal static string TestPath = null;
        static string StatePath { get { return TestPath ?? Home.State("switches.json"); } }

        static string DayOf(DateTime t) { return t.AddHours(-4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        public static int Today { get { lock (gate) { Load(); Roll(DateTime.Now); return today; } } }

        public static int LastHour
        {
            get { lock (gate) { Load(); Trim(DateTime.Now); return recent.Count; } }
        }

        /// <summary>What's in front of them right now (key = window, plus the tab in a browser; cat and title from
        /// Behavior). Null key: they're away or it's Bestie's own window, which doesn't count and doesn't break a streak.</summary>
        internal static void Saw(DateTime now, string key, bool away) { Saw(now, key, IntPtr.Zero, null, null, away); }

        internal static void Saw(DateTime now, string key, IntPtr window, string cat, string title, bool away)
        {
            bool changed = false;
            lock (gate)
            {
                Load();
                if (Roll(now)) changed = true;
                if (away) { lastKey = null; lastWindow = IntPtr.Zero; }   // coming back to the computer isn't a switch
                else if (key != null)
                {
                    if (lastKey != null && key != lastKey && IsOffTask(cat, title) && !SideBySide(lastWindow, window))
                    {
                        today++;
                        recent.Add(now);
                        Trim(now);
                        changed = true;
                    }
                    lastKey = key;
                    lastWindow = window;
                }
                if (changed) Save();
            }
            var h = Changed;
            if (changed && h != null) try { h(); } catch { }
        }

        /// <summary>For the assistant's prompt: one line, or null before the first switch today.</summary>
        public static string StatusLine()
        {
            int t = Today, h = LastHour;
            string w = WarnEnabled ? " you call it out at " + WarnCount + " switches in " + WarnWindowMinutes + " minutes." : " the too-many-switches warning is OFF.";
            if (t == 0) return WarnEnabled ? null : w.Trim();
            return "off-task switches today (since 4 am): " + t + "; in the last hour: " + h + " (the switches widget shows this live; only a"
                + " move to video, social, games or chat counts, not editor/terminal/search/AI moves, a tutorial video, coming back to work, or a"
                + " window already up on another monitor)." + w;
        }

        static bool Roll(DateTime now)
        {
            string d = DayOf(now);
            if (d == day) return false;
            day = d; today = 0; recent.Clear();
            return true;
        }

        static void Trim(DateTime now) { recent.RemoveAll(t => (now - t).TotalMinutes >= 60 || t > now.AddMinutes(1)); }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(StatePath)) return;
                var o = Json.Obj(File.ReadAllText(StatePath, Encoding.UTF8));
                if (o == null) return;
                warn = Json.Bool(o, "warn", true);
                if ((int)Json.Num(o, "rule", 1) != Rule) return;   // counted the old way: today starts over
                day = Json.Str(o, "day");
                today = (int)Json.Num(o, "today", 0);
                object[] r = Json.Arr(o, "recent");
                if (r != null)
                    foreach (object x in r)
                    {
                        DateTime t;
                        if (x is string && DateTime.TryParse((string)x, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t)) recent.Add(t);
                    }
            }
            catch (Exception ex) { DebugLog.Write("switches: load: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                var o = new Dictionary<string, object> { { "rule", Rule }, { "day", day }, { "today", today }, { "warn", warn },
                    { "recent", recent.Select(t => (object)t.ToString("o", CultureInfo.InvariantCulture)).ToArray() } };
                File.WriteAllText(StatePath, Json.Write(o), new UTF8Encoding(false));
            }
            catch (Exception ex) { DebugLog.Write("switches: save: " + ex.Message); }
        }
    }

    // ======================================================================== the widget

    public sealed class SwitchesWidget : WidgetForm
    {
        readonly Timer tick = new Timer { Interval = 1000 };
        readonly Action changed;
        int shownToday = -1, shownHour = -1, shownRecent = -1;
        static readonly Color Hot = Color.FromArgb(232, 74, 120);   // time blocks' break pink

        protected override string Title { get { return "switches"; } }
        protected override string Subtitle { get { return "since 4 am"; } }

        public SwitchesWidget(WidgetState st) : base(st)
        {
            changed = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(Refresh)); } catch { } };
            SwitchCount.Changed += changed;
            tick.Tick += (s, e) => Refresh();   // the last hour also drops old switches with no new ones
            tick.Start();
        }

        new void Refresh()
        {
            int t = SwitchCount.Today, h = SwitchCount.LastHour, r = SwitchCount.InLast(SwitchCount.WarnWindowMinutes);
            if (t == shownToday && h == shownHour && r == shownRecent) return;
            shownToday = t; shownHour = h; shownRecent = r;
            Invalidate();
        }

        protected override void PaintBody(Graphics g, Rectangle body)
        {
            int t = SwitchCount.Today, h = SwitchCount.LastHour, r = SwitchCount.InLast(SwitchCount.WarnWindowMinutes);
            bool hopping = r >= SwitchCount.WarnCount;
            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var ink = new SolidBrush(hopping ? Hot : Ink))
            using (var faint = new SolidBrush(Faint))
            using (var hot = new SolidBrush(Hot))
            using (var acc = new SolidBrush(WidgetManager.Accent))
            {
                float y = body.Y;
                g.DrawString(t.ToString(CultureInfo.InvariantCulture), Renderer.GetFont("Segoe UI Light", 40 * k, FontStyle.Regular), ink,
                    new RectangleF(body.X, y, body.Width, 58 * k), center);
                string caption = hopping ? "SWITCHING A LOT · " + r + " IN " + SwitchCount.WarnWindowMinutes + " MIN"
                    : t == 1 ? "OFF-TASK SWITCH TODAY" : "OFF-TASK SWITCHES TODAY";
                g.DrawString(caption, Renderer.GetFont("Segoe UI Semibold", 9 * k, FontStyle.Regular), hopping ? hot : faint,
                    new RectangleF(body.X, y + 56 * k, body.Width, 16 * k), center);
                float ly = y + 82 * k;
                using (var line = new Pen(Color.FromArgb(40, Faint), 1)) g.DrawLine(line, body.X + 10 * k, ly, body.Right - 10 * k, ly);
                g.DrawString(h.ToString(CultureInfo.InvariantCulture), Renderer.GetFont("Segoe UI Semibold", 15 * k, FontStyle.Regular), acc,
                    new RectangleF(body.X, ly + 6 * k, body.Width / 2f - 4 * k, 26 * k), new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });
                g.DrawString("in the last hour", Renderer.GetFont("Segoe UI", 10.5f * k, FontStyle.Regular), faint,
                    new RectangleF(body.X + body.Width / 2f + 2 * k, ly + 6 * k, body.Width / 2f, 26 * k), new StringFormat { LineAlignment = StringAlignment.Center });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { SwitchCount.Changed -= changed; tick.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
