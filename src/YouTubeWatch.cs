// YouTubeWatch.cs - "tell me when I go on youtube so you can get mad at me": when YouTube comes to the front (the
// focused browser tab, not a tab sitting in the background), Bestie calls it out once, stern but warm, and tells them to
// get back to it. Ten minutes between callouts, nothing during quiet time or a focus-block break, never on the YouTube
// that was already up when Bestie started (no retroactive scolding). They can take a timed break ("youtube break, 20
// minutes") or switch it off. It only talks: nothing is blocked or closed. Survives a restart. C# 5 only.
using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Bestie
{
    internal static class YouTubeWatch
    {
        const double CooldownMinutes = 10;
        const double AskedGraceSeconds = 120;   // they asked Bestie to open youtube: that's on purpose

        static readonly object gate = new object();
        static bool enabled = true;
        static DateTime breakUntil = DateTime.MinValue;   // local time
        static DateTime askedAt = DateTime.MinValue;
        static DateTime hintedDay = DateTime.MinValue;
        static readonly Tracker tracker = new Tracker();

        static string StatePath { get { return Home.State("youtube.txt"); } }

        public static bool Enabled { get { lock (gate) return enabled; } }
        public static bool OnBreak { get { lock (gate) return DateTime.Now < breakUntil; } }
        public static DateTime BreakUntil { get { lock (gate) return breakUntil; } }

        // ------------------------------------------------------------------ settings

        public static void Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                string[] parts = File.ReadAllText(StatePath).Trim().Split('|');
                long ticks;
                lock (gate)
                {
                    enabled = parts[0] != "off";
                    if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
                        breakUntil = new DateTime(ticks, DateTimeKind.Local);
                }
            }
            catch (Exception ex) { DebugLog.Write("youtube: load: " + ex.Message); }
        }

        static void Save()
        {
            string line;
            lock (gate) line = (enabled ? "on" : "off") + "|" + breakUntil.Ticks.ToString(CultureInfo.InvariantCulture);
            try { File.WriteAllText(StatePath, line); } catch (Exception ex) { DebugLog.Write("youtube: save: " + ex.Message); }
        }

        public static void SetEnabled(bool on)
        {
            lock (gate) { enabled = on; if (!on) breakUntil = DateTime.MinValue; }
            Save();
            DebugLog.Write("youtube: callouts " + (on ? "on" : "off"));
        }

        /// <summary>A YouTube break for this many minutes from now (0 or less: the break is over).</summary>
        public static void Break(int minutes)
        {
            lock (gate) breakUntil = minutes > 0 ? DateTime.Now.AddMinutes(Math.Min(minutes, 12 * 60)) : DateTime.MinValue;
            Save();
            DebugLog.Write(minutes > 0 ? "youtube: break until " + When.Clock(BreakUntil) : "youtube: break over");
        }

        /// <summary>Bestie itself is opening YouTube because they asked: don't scold them for it.</summary>
        public static void Asked()
        {
            lock (gate) askedAt = DateTime.Now;
        }

        /// <summary>One line for the assistant's context, or null when it's just on and watching.</summary>
        public static string StatusLine()
        {
            if (!Enabled) return "youtube callouts are OFF (they switched them off).";
            if (OnBreak) return "they're on a youtube break until " + When.Clock(BreakUntil) + " (no callouts till then).";
            return null;
        }

        // ------------------------------------------------------------------ watching

        /// <summary>Call about once a second with what's in front of them (Behavior.Current; known false when it has no
        /// fresh look yet or they're away). Returns the line to say, or null. Any thread.</summary>
        public static string Tick(bool known, string app, string site, string title)
        {
            DateTime now = DateTime.Now;
            bool on = known && IsYouTube(app, site, title);
            lock (gate) if (!known && !tracker.Primed) return null;   // the first real look decides what was already up
            bool afterBreak, hint;
            lock (gate)
            {
                bool breakOn = now < breakUntil;
                bool breakEnded = tracker.WasOnBreak && !breakOn;
                tracker.WasOnBreak = breakOn;
                bool asked = (now - askedAt).TotalSeconds < AskedGraceSeconds;
                bool allowed = enabled && !breakOn && !asked && !Quiet.On && !(TimeBlocks.Running && TimeBlocks.Phase == "break");
                if (!tracker.Step(now, on, allowed, breakEnded)) return null;
                afterBreak = tracker.AfterBreak;
                hint = hintedDay != now.Date;
                hintedDay = now.Date;
            }
            string task = TimeBlocks.Running && TimeBlocks.Phase == "focus" && !TimeBlocks.Paused
                ? "your focus block (" + Math.Max(1, (int)Math.Ceiling(TimeBlocks.Left.TotalMinutes)) + " min left)" : null;
            string line = Callout(afterBreak, task, now);
            if (hint) line += " (on a real break? say “youtube break, 20 minutes”.)";
            DebugLog.Write("youtube: called it out");
            Journal.Add("caught you on youtube" + (afterBreak ? " after your break ran out" : "") + ". told you to get off it.");
            return line;
        }

        static readonly Regex TitleSaysYouTube = new Regex(@"(^|\s[-–—]\s)YouTube$", RegexOptions.CultureInvariant);

        /// <summary>Is YouTube the thing in front of them? The focused tab's site when the browser bridge knows it; a
        /// browser window titled "... - YouTube" when it doesn't. YouTube Music and Studio don't count.</summary>
        internal static bool IsYouTube(string app, string site, string title)
        {
            if (!string.IsNullOrEmpty(site))
            {
                string s = site.ToLowerInvariant();
                if (s == "music.youtube.com" || s == "studio.youtube.com") return false;
                return s == "youtube.com" || s == "youtu.be" || s.EndsWith(".youtube.com", StringComparison.Ordinal);
            }
            if (string.IsNullOrEmpty(title) || Array.IndexOf(Browsers, (app ?? "").ToLowerInvariant()) < 0) return false;
            return TitleSaysYouTube.IsMatch(title.Trim());
        }

        static readonly string[] Browsers = { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "arc" };

        static string Callout(bool breakEnded, string task, DateTime now)
        {
            string back = task ?? "what you were doing";
            if (breakEnded)
                return Pick(now,
                    "break's over. that was the deal. youtube off, back to " + back + ".",
                    "time. the youtube break is done. close it and get back to " + back + ". you earned the break, now earn the next one.");
            return Pick(now,
                "hey. youtube? no. get off it and get back to " + back + ". i believe in you, just not in this tab.",
                "i'm writing you up. youtube, during work hours. off you go, back to " + back + ".",
                "that's youtube. you know it's youtube. i know it's youtube. close it. back to " + back + ".",
                "nope. not youtube. you were doing good. get back to " + back + ".",
                "the big outside is calling and it's youtube. hang up. back to " + back + ". 2 out of 7 for this choice.");
        }

        static string Pick(DateTime now, params string[] lines) { return lines[(int)(now.Ticks / TimeSpan.TicksPerSecond % lines.Length)]; }

        /// <summary>The entry rules, with no clock or settings of its own (so it can be tested).</summary>
        internal sealed class Tracker
        {
            public const double DwellSeconds = 4;   // alt-tab flicker past a youtube window isn't going on youtube
            bool primed, there, pending, pendingAfterBreak;
            DateTime since, lastCallout = DateTime.MinValue;
            public bool WasOnBreak;
            public bool Primed { get { return primed; } }
            /// <summary>The last callout was for a break running out (not for coming in).</summary>
            public bool AfterBreak { get; private set; }

            /// <summary>True when this moment deserves a callout: youtube came to the front and stayed DwellSeconds, it's
            /// allowed, and the last callout was over CooldownMinutes ago. Whatever was up at the very first step counts
            /// as already seen. A break running out while they're still on it counts as coming in again.</summary>
            public bool Step(DateTime now, bool onYouTube, bool allowed, bool breakEnded)
            {
                if (!primed) { primed = true; there = onYouTube; return false; }
                if (!onYouTube) { there = false; pending = false; return false; }
                if (!there || breakEnded) { pendingAfterBreak = there && breakEnded; there = true; pending = true; since = now; }
                if (!pending || (now - since).TotalSeconds < DwellSeconds) return false;
                pending = false;   // an entry that wasn't allowed (break, quiet) is let go, not saved for later
                if (!allowed || (now - lastCallout).TotalMinutes < CooldownMinutes) return false;
                lastCallout = now;
                AfterBreak = pendingAfterBreak;
                return true;
            }
        }
    }
}
