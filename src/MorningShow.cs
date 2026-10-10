// MorningShow.cs - Locked In FM's morning show: a minute after their alarm the computer wakes itself up (it sleeps at night;
// a windows wake task does it, screen stays dark), connects to the echo as a bluetooth speaker, starts spotify and goes on the
// air with a good-morning show (the weather and any storm alerts, today's calendar, reminders), then signs off, lets go of the
// echo and lets the computer sleep again. Set by voice ("my alarm is 6:30 on weekdays"). Saved in .state\morning-show.json.
// C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Bestie
{
    internal sealed class MorningShow
    {
        public bool Enabled;
        public string Alarm = "";          // "06:30"
        public string Days = "weekdays";   // as When.ParseRepeat takes it: daily, weekdays, weekends, mon,thu...
        public int DelayMinutes = 1, Minutes = 45;
        public string Speaker = "Party Time", Music = "";
        public string LastRun = "";        // yyyy-MM-dd

        const string TaskName = "Bestie morning show";
        static string FilePath { get { return Home.State("morning-show.json"); } }
        static readonly object gate = new object();
        static volatile bool running;

        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_AWAYMODE_REQUIRED = 0x40;

        public static MorningShow Load()
        {
            var m = new MorningShow();
            try
            {
                if (!File.Exists(FilePath)) return m;
                var d = Json.Obj(File.ReadAllText(FilePath, Encoding.UTF8));
                m.Enabled = Json.Bool(d, "enabled", false);
                m.Alarm = Json.Str(d, "alarm") ?? "";
                m.Days = Json.Str(d, "days") ?? "weekdays";
                m.DelayMinutes = (int)Json.Num(d, "delayMinutes", 1);
                m.Minutes = (int)Json.Num(d, "minutes", 45);
                m.Speaker = Json.Str(d, "speaker") ?? "Party Time";
                m.Music = Json.Str(d, "music") ?? "";
                m.LastRun = Json.Str(d, "lastRun") ?? "";
            }
            catch (Exception ex) { DebugLog.Write("morning show: load: " + ex.Message); }
            return m;
        }

        public void Save()
        {
            var d = new Dictionary<string, object> { { "enabled", Enabled }, { "alarm", Alarm }, { "days", Days }, { "delayMinutes", DelayMinutes },
                { "minutes", Minutes }, { "speaker", Speaker }, { "music", Music }, { "lastRun", LastRun } };
            File.WriteAllText(FilePath, Json.Write(d), new UTF8Encoding(false));
        }

        /// <summary>"6:30 am" / "6:30" / "18:30" -> (hour, minute), or false.</summary>
        static bool ParseAlarm(string s, out int h, out int m)
        {
            h = m = 0;
            var x = System.Text.RegularExpressions.Regex.Match((s ?? "").Trim().ToLowerInvariant(), @"^(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)?$");
            if (!x.Success) return false;
            h = int.Parse(x.Groups[1].Value); m = x.Groups[2].Success ? int.Parse(x.Groups[2].Value) : 0;
            string ap = x.Groups[3].Value;
            if (ap.StartsWith("p") && h < 12) h += 12;
            if (ap.StartsWith("a") && h == 12) h = 0;
            return h < 24 && m < 60;
        }

        bool OnDay(DateTime d)
        {
            string r = When.ParseRepeat(Days, d);
            return !string.IsNullOrEmpty(r) && When.RepeatsOn(r, d.DayOfWeek);
        }

        /// <summary>When today's show starts (alarm + delay), or null if it doesn't run today.</summary>
        DateTime? ShowTimeOn(DateTime day)
        {
            int h, m;
            if (!Enabled || !ParseAlarm(Alarm, out h, out m) || !OnDay(day)) return null;
            return day.Date.AddHours(h).AddMinutes(m + DelayMinutes);
        }

        public string Describe()
        {
            if (!Enabled) return "the morning show is off.";
            DateTime? next = null;
            for (int i = 0; i < 8 && next == null; i++)
            {
                DateTime? t = ShowTimeOn(DateTime.Today.AddDays(i));
                if (t != null && t.Value > DateTime.Now) next = t;
            }
            return "the morning show is on: alarm " + Alarm + " (" + When.DescribeRepeat(When.ParseRepeat(Days, DateTime.Today) ?? Days) + "), the show starts " + DelayMinutes
                + " min later on " + Speaker + " for " + Minutes + " minutes" + (Music.Length > 0 ? ", playing " + Music : "")
                + (next != null ? ". next one: " + next.Value.ToString("dddd h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() : "") + ".";
        }

        // ------------------------------------------------------------------ waking the computer

        /// <summary>The windows task that wakes the computer a minute before the alarm (it only wakes it; bestie does the
        /// show). Null when it's set; else what went wrong.</summary>
        public string SetWakeTask()
        {
            int h, m;
            if (!ParseAlarm(Alarm, out h, out m)) return "i don't understand the alarm time \"" + Alarm + "\".";
            DateTime wake = DateTime.Today.AddHours(h).AddMinutes(m).AddMinutes(-1);
            string r = When.ParseRepeat(Days, DateTime.Today) ?? "daily";
            var days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" }
                .Where((name, i) => When.RepeatsOn(r, (DayOfWeek)i)).ToArray();
            if (days.Length == 0) days = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>Wakes the computer for Bestie's morning show (Locked In FM). Bestie does the rest.</Description></RegistrationInfo>\r\n" +
"  <Triggers><CalendarTrigger><StartBoundary>" + wake.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "</StartBoundary><Enabled>true</Enabled>" +
"<ScheduleByWeek><DaysOfWeek>" + string.Concat(days.Select(d => "<" + d + " />").ToArray()) + "</DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>" + System.Security.SecurityElement.Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
"  <Settings><WakeToRun>true</WakeToRun><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
"<StartWhenAvailable>false</StartWhenAvailable><ExecutionTimeLimit>PT5M</ExecutionTimeLimit><Enabled>true</Enabled><Hidden>false</Hidden></Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(Path.Combine(Environment.SystemDirectory, "cmd.exe")) + "</Command><Arguments>/c exit 0</Arguments></Exec></Actions>\r\n" +
"</Task>\r\n";
            string file = Home.State("morning-show-task.xml");
            File.WriteAllText(file, xml, Encoding.Unicode);
            string output;
            int code = Run("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + file + "\" /F", out output);
            DebugLog.Write("morning show: wake task -> " + code + " " + output.Trim());
            return code == 0 ? null : "windows wouldn't make the wake-up task: " + output.Trim();
        }

        public static void RemoveWakeTask()
        {
            string output;
            Run("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", out output);
            DebugLog.Write("morning show: wake task removed (" + output.Trim() + ")");
        }

        public static bool WakeTaskExists()
        {
            string output;
            return Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", out output) == 0;
        }

        static int Run(string exe, string args, out string output)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                return p.ExitCode;
            }
        }

        // ------------------------------------------------------------------ the show

        /// <summary>Every ~20 s from the app: time for the show? (also right after the computer wakes up).</summary>
        public static void Tick(AssistantModule module)
        {
            if (running || module == null) return;
            MorningShow m = Load();
            DateTime? t = m.ShowTimeOn(DateTime.Today);
            if (t == null || DateTime.Now < t.Value || DateTime.Now > t.Value.AddMinutes(30)) return;
            if (m.LastRun == DateTime.Today.ToString("yyyy-MM-dd")) return;
            m.LastRun = DateTime.Today.ToString("yyyy-MM-dd");
            m.Save();
            Start(module, m, false);
        }

        /// <summary>Runs the show now (the scheduled one, or "test the morning show").</summary>
        public static string Start(AssistantModule module, MorningShow m, bool test)
        {
            lock (gate)
            {
                if (running) return "the morning show is already on.";
                running = true;
            }
            new Thread(delegate() { try { Show(module, m, test); } catch (Exception ex) { DebugLog.Write("morning show: " + ex); } finally { running = false; } })
                { IsBackground = true, Name = "bestie-morning-show" }.Start();
            return null;
        }

        static void Show(AssistantModule module, MorningShow m, bool test)
        {
            // keep the computer awake (screen can stay dark) while the show is on
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
            string before = null;
            BtAudio.Speaker speaker = null;
            try
            {
                DebugLog.Write("morning show: starting" + (test ? " (test)" : ""));
                try { before = SoundOutput.List().Where(d => d.Default).Select(d => d.Id).FirstOrDefault(); } catch { }
                string problem = BtAudio.Connect(m.Speaker, out speaker);
                if (problem != null)
                {
                    // no bluetooth: at least a spoken good-morning on the echo
                    DebugLog.Write("morning show: " + problem);
                    AssistantSettings s = module.CurrentSettings;
                    if (s.AlexaToken.Length > 0)
                        Alexa.Say(s.AlexaToken, s.AlexaDevice, "good morning. locked in fm couldn't reach this speaker over bluetooth, so here's the short version. " + Briefing());
                    return;
                }
                SoundOutput.SetDefault(speaker.Id);
                DebugLog.Write("morning show: playing through " + speaker.Name);
                Thread.Sleep(1500);
                string music = SpotifyPlayer.Play("playlist", m.Music.Length > 0 ? m.Music : "good morning", "");
                DebugLog.Write("morning show: music: " + OpenAIClient.Trunc(music, 120));
                Thread.Sleep(2500);
                LockedInFM radio = module.Radio;
                string startProblem = radio.StartShow("morning");
                if (startProblem != null) { DebugLog.Write("morning show: " + startProblem); return; }
                var until = DateTime.Now.AddMinutes(test ? Math.Min(m.Minutes, 3) : m.Minutes);
                while (DateTime.Now < until && radio.On) Thread.Sleep(5000);
                if (radio.On)
                {
                    radio.SignOff();   // the outro, then off the air
                    ComputerAgent.Input.Press(0xB3);   // spotify was the one playing: pause it
                }
            }
            finally
            {
                try { if (before != null && speaker != null && before != speaker.Id) SoundOutput.SetDefault(before); } catch { }
                if (speaker != null) { Thread.Sleep(1000); BtAudio.Disconnect(speaker.Id); }
                SetThreadExecutionState(ES_CONTINUOUS);   // the computer may sleep again (if no one's using it)
                DebugLog.Write("morning show: done");
            }
        }

        public static string PairingState(string speaker)
        {
            try
            {
                BtAudio.Speaker s = BtAudio.Find(speaker);
                return s == null ? speaker + " isn't paired with this computer yet (one time: settings > bluetooth > add device while the echo is in pairing mode: \"alexa, pair\")."
                    : s.Name + " is paired" + (s.Connected ? " and connected right now." : "; bestie connects to it for the show.");
            }
            catch (Exception ex) { return "couldn't check bluetooth: " + ex.Message; }
        }

        /// <summary>A few sentences for the echo when bluetooth isn't there: the weather, alerts, and the calendar.</summary>
        static string Briefing()
        {
            var sb = new StringBuilder();
            WeatherReport w = Weather.Now();
            if (w != null) sb.Append("it's ").Append((int)Math.Round(w.TempF)).Append(" degrees and ").Append(w.Sky).Append(", high of ").Append((int)Math.Round(w.HighF)).Append(". ");
            if (w != null && w.Alerts.Count > 0) sb.Append("weather alerts: ").Append(string.Join(", ", w.Alerts.Select(a => a.Event + " until " + When.Clock(a.Expires)).ToArray())).Append(". ");
            string cal = IcsCalendar.Configured ? IcsCalendar.ContextLine() : null;
            if (cal != null) sb.Append(OpenAIClient.Trunc(cal, 200));
            return sb.ToString();
        }
    }

    internal sealed partial class AssistantTools
    {
        void AddMorningShowTools(List<object> t)
        {
            t.Add(Tool("morning_show", "Locked In FM's morning show: right after their alarm the computer wakes itself (it sleeps at night), plays through their echo "
                + "as a bluetooth speaker, starts spotify and you host a good-morning show (weather and storm alerts, today's calendar, reminders), then it signs "
                + "off and the computer sleeps again. set it when they tell you their alarm ('my alarm is 6:30 on weekdays'); off; status (also says if the echo is "
                + "paired); test runs a 3-minute show now.", R("action"),
                PE("action", "what to do", new[] { "set", "off", "status", "test" }),
                P("alarm", "string", "set: their alarm time as they said it, e.g. '6:30 am'"),
                P("days", "string", "set: daily, weekdays, weekends, or days like 'mon,wed,fri' (default weekdays)"),
                P("minutes", "number", "set: how long the show runs (default 45)"),
                P("music", "string", "set: what spotify plays, e.g. 'my morning playlist', 'lofi beats' (default a good-morning playlist)"),
                P("speaker", "string", "set: the bluetooth speaker (default Party Time)")));
        }

        AssistantToolResult MorningShowTool(string name, Dictionary<string, object> a)
        {
            if (name != "morning_show") return null;
            MorningShow m = MorningShow.Load();
            string action = Json.Str(a, "action") ?? "status";
            if (DryRun) return new AssistantToolResult("done.");
            switch (action)
            {
                case "set":
                    {
                        string alarm = (Json.Str(a, "alarm") ?? m.Alarm).Trim();
                        if (alarm.Length == 0) return new AssistantToolResult("what time is their alarm?");
                        m.Alarm = alarm;
                        if (!string.IsNullOrWhiteSpace(Json.Str(a, "days"))) m.Days = Json.Str(a, "days").Trim();
                        if (When.ParseRepeat(m.Days, DateTime.Today) == null) return new AssistantToolResult("days should be daily, weekdays, weekends or days like mon,wed,fri.");
                        double minutes = Json.Num(a, "minutes", 0);
                        if (minutes > 0) m.Minutes = (int)Math.Max(5, Math.Min(180, minutes));
                        if (Json.Str(a, "music") != null) m.Music = Json.Str(a, "music").Trim();
                        if (!string.IsNullOrWhiteSpace(Json.Str(a, "speaker"))) m.Speaker = Json.Str(a, "speaker").Trim();
                        m.Enabled = true;
                        string task = m.SetWakeTask();
                        if (task != null) return new AssistantToolResult(task);
                        m.Save();
                        Journal.Add("i set up the morning show: " + m.Describe());
                        return new AssistantToolResult(m.Describe() + " the computer wakes itself a minute before the alarm (it has to be asleep, not shut down). "
                            + MorningShow.PairingState(m.Speaker) + " tell them in one or two sentences.", "morning show: " + m.Alarm, null);
                    }
                case "off":
                    m.Enabled = false;
                    m.Save();
                    MorningShow.RemoveWakeTask();
                    return new AssistantToolResult("the morning show is off, and the computer won't wake itself anymore.", "morning show: off", null);
                case "test":
                    {
                        string p = MorningShow.Start(module, m.Enabled ? m : new MorningShow { Speaker = m.Speaker, Music = m.Music }, true);
                        return new AssistantToolResult(p ?? "running a 3-minute morning show now on " + m.Speaker + " (connecting bluetooth takes a few seconds). say almost nothing; the show talks.");
                    }
                default:
                    return new AssistantToolResult(m.Describe() + " " + MorningShow.PairingState(m.Speaker) + (m.Enabled && !MorningShow.WakeTaskExists() ? " (the wake-up task is missing: set it again.)" : ""));
            }
        }
    }
}
