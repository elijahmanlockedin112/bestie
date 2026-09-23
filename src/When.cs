using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bestie
{
    /// <summary>Turns what people say ("6:45 am", "tomorrow", "friday", "sep 24", "18:45") into a real local time,
    /// so the model never has to do clock arithmetic (it gets it wrong: "6:45" became 6:34).</summary>
    public static class When
    {
        static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>Local due time from a date phrase and a clock time, relative to now. Either may be empty.
        /// Returns null and sets error when it can't make sense of them. assumed: a note when it had to guess am/pm.</summary>
        public static DateTime? Parse(string date, string time, DateTime now, out string error, out string assumed)
        {
            error = null; assumed = null;
            string s = " " + ((date ?? "") + " " + (time ?? "")).ToLowerInvariant().Replace(",", " ").Replace("’", "'") + " ";
            s = Regex.Replace(s, @"\s+", " ");
            DateTime? day = null;
            bool dayIsWeekday = false;

            // 2026-09-24 or 2026-09-24T06:45
            Match m = Regex.Match(s, @"(\d{4})-(\d{1,2})-(\d{1,2})(?:t|\s)?");
            if (m.Success)
            {
                day = SafeDate(Int(m.Groups[1]), Int(m.Groups[2]), Int(m.Groups[3]));
                if (day == null) { error = "that date doesn't exist."; return null; }
                s = s.Remove(m.Index, m.Length).Insert(m.Index, " ");
            }
            // 9/24 or 9/24/2026
            if (day == null && (m = Regex.Match(s, @"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b")).Success)
            {
                int y = m.Groups[3].Success ? Int(m.Groups[3]) : now.Year;
                if (y < 100) y += 2000;
                day = SafeDate(y, Int(m.Groups[1]), Int(m.Groups[2]));
                if (day == null) { error = "that date doesn't exist."; return null; }
                if (!m.Groups[3].Success && day.Value < now.Date) day = day.Value.AddYears(1);
                s = s.Remove(m.Index, m.Length).Insert(m.Index, " ");
            }
            // sep 24 / september 24th / 24 september
            if (day == null)
            {
                m = Regex.Match(s, @"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.? (\d{1,2})(?:st|nd|rd|th)?\b");
                Match m2 = Regex.Match(s, @"\b(\d{1,2})(?:st|nd|rd|th)? (?:of )?(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\b");
                if (m.Success || m2.Success)
                {
                    Match hit = m.Success ? m : m2;
                    string mon = m.Success ? m.Groups[1].Value : m2.Groups[2].Value;
                    int dd = Int(m.Success ? m.Groups[2] : m2.Groups[1]);
                    day = SafeDate(now.Year, Array.IndexOf(Months, mon) + 1, dd);
                    if (day == null) { error = "that date doesn't exist."; return null; }
                    if (day.Value < now.Date) day = day.Value.AddYears(1);
                    s = s.Remove(hit.Index, hit.Length).Insert(hit.Index, " ");
                }
            }
            if (day == null && Regex.IsMatch(s, @"\b(today|tonight|this (morning|afternoon|evening))\b")) day = now.Date;
            if (day == null && Regex.IsMatch(s, @"\b(tomorrow|tmrw|tmr)\b")) day = now.Date.AddDays(1);
            if (day == null && Regex.IsMatch(s, @"\bday after tomorrow\b")) day = now.Date.AddDays(2);
            if (day == null)
            {
                for (int i = 0; i < 7; i++)
                {
                    var dow = (DayOfWeek)i;
                    string full = dow.ToString().ToLowerInvariant();
                    if (!Regex.IsMatch(s, @"\b(" + full + "|" + full.Substring(0, 3) + @")\b")) continue;
                    int ahead = ((int)dow - (int)now.DayOfWeek + 7) % 7;
                    if (Regex.IsMatch(s, @"\bnext " + full.Substring(0, 3)) && ahead == 0) ahead = 7;
                    day = now.Date.AddDays(ahead);
                    dayIsWeekday = ahead == 0;
                    break;
                }
            }
            bool wantsAm = Regex.IsMatch(s, @"\bmorning\b");
            bool wantsPm = Regex.IsMatch(s, @"\b(afternoon|evening|tonight|night)\b");

            // the clock time
            int hour = -1, minute = 0;
            bool? pm = null;
            bool explicit24 = false;
            if (Regex.IsMatch(s, @"\bnoon\b|\bmidday\b")) { hour = 12; pm = true; }
            else if (Regex.IsMatch(s, @"\bmidnight\b")) { hour = 0; explicit24 = true; }
            else
            {
                m = Regex.Match(s, @"\b(\d{1,2})(?:[:.h](\d{2}))?(?::\d{2})?\s*(a\.? ?m\.?|p\.? ?m\.?|a|p)?(?=[\s.]|$)");
                if (m.Success)
                {
                    hour = Int(m.Groups[1]);
                    minute = m.Groups[2].Success ? Int(m.Groups[2]) : 0;
                    if (m.Groups[3].Success) pm = m.Groups[3].Value.StartsWith("p");
                    else if (hour == 0 || hour > 12 || m.Groups[1].Value.StartsWith("0")) explicit24 = true;
                    if (hour > 23 || minute > 59 || (pm != null && (hour == 0 || hour > 12))) { error = "that isn't a real clock time."; return null; }
                }
            }

            if (hour < 0)
            {
                if (day == null) { error = "i need a time (like \"6:45 pm\") or a number of minutes."; return null; }
                error = "what time on that day?";
                return null;
            }

            // candidate hours (24h) in the order to try
            int[] hours;
            if (explicit24) hours = new[] { hour };
            else if (pm == true) hours = new[] { hour == 12 ? 12 : hour + 12 };
            else if (pm == false) hours = new[] { hour == 12 ? 0 : hour };
            else if (wantsPm) hours = new[] { hour == 12 ? 12 : hour + 12 };
            else if (wantsAm) hours = new[] { hour == 12 ? 0 : hour };
            else hours = new[] { hour == 12 ? 12 : hour, hour == 12 ? 0 : hour + 12 };   // no am/pm: whichever comes next

            DateTime? best = null;
            if (day != null)
            {
                foreach (int h in hours)
                {
                    DateTime c = day.Value.AddHours(h).AddMinutes(minute);
                    if (c > now) { best = c; break; }
                }
                if (best == null && dayIsWeekday)
                    best = day.Value.AddDays(7).AddHours(hours[0]).AddMinutes(minute);
                if (best == null) { error = "that time already passed."; return null; }
                if (hours.Length > 1) assumed = "they didn't say am or pm, so i went with " + Clock(best.Value) + ".";
            }
            else
            {
                for (int add = 0; add < 2 && best == null; add++)
                    foreach (int h in hours)
                    {
                        DateTime c = now.Date.AddDays(add).AddHours(h).AddMinutes(minute);
                        if (c > now) { best = c; break; }
                    }
                if (hours.Length > 1) assumed = "they didn't say am or pm, so i picked the next " + Clock(best.Value) + ".";
            }
            return best;
        }

        /// <summary>"6:45 am"</summary>
        public static string Clock(DateTime local)
        {
            return local.ToString(local.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        }

        /// <summary>"today at 6:45 pm", "tomorrow (thursday) at 6:45 am", "friday sep 26 at 9 am"</summary>
        public static string Describe(DateTime local, DateTime now)
        {
            string c = Clock(local);
            if (local.Date == now.Date) return "today at " + c;
            if (local.Date == now.Date.AddDays(1)) return "tomorrow (" + local.ToString("dddd", CultureInfo.InvariantCulture).ToLowerInvariant() + ") at " + c;
            return local.ToString("dddd MMM d", CultureInfo.InvariantCulture).ToLowerInvariant() + " at " + c;
        }

        /// <summary>"in 13 h 25 min"</summary>
        public static string Until(DateTime local, DateTime now)
        {
            TimeSpan d = local - now;
            int mins = (int)Math.Round(d.TotalMinutes);
            if (mins < 1) return "in under a minute";
            if (mins < 60) return "in " + mins + " min";
            if (mins < 60 * 48) return "in " + (mins / 60) + " h" + (mins % 60 > 0 ? " " + (mins % 60) + " min" : "");
            return "in " + (int)Math.Round(d.TotalDays) + " days";
        }

        static int Int(Group g) { return int.Parse(g.Value, CultureInfo.InvariantCulture); }

        static DateTime? SafeDate(int y, int mo, int d)
        {
            if (mo < 1 || mo > 12 || d < 1 || y < 2000 || y > 2200 || d > DateTime.DaysInMonth(y, mo)) return null;
            return new DateTime(y, mo, d);
        }
    }
}
