// Quiet.cs - "be quiet for an hour": while it's on, Bestie doesn't talk out loud on its own. Reminders and
// heads-ups still show, silently, in its bubble; FYIs ("claude finished X") wait and come as one short summary
// when quiet time is over. Survives a restart. C# 5 only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Bestie
{
    internal static class Quiet
    {
        static readonly object gate = new object();
        static DateTime until = DateTime.MinValue;   // local time
        static readonly List<string> held = new List<string>();

        static string StatePath { get { return Home.State("quiet.txt"); } }

        public static bool On { get { lock (gate) return DateTime.Now < until; } }
        public static DateTime Until { get { lock (gate) return until; } }

        public static void Load()
        {
            try
            {
                long ticks;
                if (File.Exists(StatePath) && long.TryParse(File.ReadAllText(StatePath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ticks))
                    lock (gate) until = new DateTime(ticks, DateTimeKind.Local);
            }
            catch (Exception ex) { DebugLog.Write("quiet: load: " + ex.Message); }
        }

        /// <summary>Quiet for this many minutes from now (0 or less: talk again).</summary>
        public static void For(int minutes)
        {
            lock (gate) until = minutes > 0 ? DateTime.Now.AddMinutes(Math.Min(minutes, 24 * 60)) : DateTime.MinValue;
            try { File.WriteAllText(StatePath, Until.Ticks.ToString(CultureInfo.InvariantCulture)); } catch { }
            DebugLog.Write(minutes > 0 ? "quiet: until " + When.Clock(Until) : "quiet: off");
        }

        /// <summary>Something it would have said (short: "chatgpt finished “x”"), to sum up afterwards.</summary>
        public static void Hold(string brief)
        {
            if (string.IsNullOrWhiteSpace(brief)) return;
            lock (gate) if (held.Count < 40) held.Add(brief.Trim().TrimEnd('.'));
            DebugLog.Write("quiet: held \"" + brief + "\"");
        }

        /// <summary>Once quiet time is over (or always, with force): one line about what it held back, or null if
        /// nothing was. Clears what it held.</summary>
        public static string TakeSummary(bool force)
        {
            lock (gate)
            {
                if ((!force && DateTime.Now < until) || held.Count == 0) return null;
                var groups = held.GroupBy(h => h).Select(g => g.Count() > 1 ? g.Key + " (" + g.Count() + " times)" : g.Key).ToList();
                held.Clear();
                string list = string.Join("; ", groups.Take(3).ToArray()) + (groups.Count > 3 ? "; and " + (groups.Count - 3) + " more" : "");
                return "quiet time's over. while you were busy: " + list + ".";
            }
        }
    }
}
