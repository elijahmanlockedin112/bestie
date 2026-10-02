// Shared glue between the creature, the assistant and the browser bridge.
// C# 5 only (compiled with the .NET Framework 4.x csc that ships with Windows).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Bestie
{
    /// <summary>
    /// What the assistant side may ask of the creature. Always call on the UI thread
    /// (use Control.BeginInvoke from background threads).
    /// </summary>
    public interface ICreatureHost
    {
        /// <summary>Its name was really said (confirmed by the transcript): stop, glow, look up.</summary>
        void Heard();
        string CreatureName { get; }

        /// <summary>A few lines on the creature's personality and what it has learned
        /// (its nicknames for apps, rituals, favorite spots, how it feels about the user).</summary>
        string PersonalitySummary();

        /// <summary>Compact snapshot of the desktop: local time, foreground window, open windows
        /// (app + title, top of z-order first), monitors, whether the user is idle.</summary>
        string DesktopContext();

        /// <summary>Show a speech bubble over the creature. seconds &lt;= 0 picks a length from the text.</summary>
        void Say(string text, double seconds);

        /// <summary>"idle", "thinking", "listening" or "talking". Drives the creature's animation.</summary>
        void SetMood(string mood);

        /// <summary>Ask the creature to do one of its tricks (see TrickNames).
        /// Returns false if the trick is unknown or it can't right now (e.g. being held).</summary>
        bool DoTrick(string trick);

        string[] TrickNames { get; }

        /// <summary>The creature's feet, in physical screen pixels.</summary>
        Point CreatureScreenPoint { get; }

        /// <summary>Work area of the monitor the creature is currently on.</summary>
        Rectangle CreatureMonitorArea { get; }
    }

    /// <summary>The creature's own folder on disk (Documents\Bestie). Everything it writes lives here.</summary>
    public static class Home
    {
        public static string Root { get; private set; }
        public static string MemoryDir { get; private set; }   // long-term memory the assistant reads (about-you.md, ...)
        public static string JournalDir { get; private set; }  // one markdown file per day of observations
        public static string NotesDir { get; private set; }    // notes the assistant writes when asked
        public static string ChatsDir { get; private set; }    // chat transcripts, one file per day
        public static string StateDir { get; private set; }    // machine stuff: brain.json, settings.json, logs

        public static void Init()
        {
            if (Root != null) return;
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Root = Path.Combine(docs, "Bestie");
            MemoryDir = Path.Combine(Root, "memory");
            JournalDir = Path.Combine(Root, "journal");
            NotesDir = Path.Combine(Root, "notes");
            ChatsDir = Path.Combine(Root, "chats");
            StateDir = Path.Combine(Root, ".state");
            foreach (string d in new[] { Root, MemoryDir, JournalDir, NotesDir, ChatsDir, StateDir })
                Directory.CreateDirectory(d);
            try { new DirectoryInfo(StateDir).Attributes |= FileAttributes.Hidden; } catch { }

            string readme = Path.Combine(Root, "README.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    "this is my folder. i live on your screen and i write things down here.\r\n\r\n" +
                    "  memory\\   things i know about you (you can edit these, i'll read them)\r\n" +
                    "  journal\\  what i noticed each day\r\n" +
                    "  notes\\    notes you asked me to keep\r\n" +
                    "  chats\\    what we talked about\r\n" +
                    "  .state\\   my brain. please don't poke it.\r\n", Encoding.UTF8);
            }
        }

        public static string State(string fileName) { Init(); return Path.Combine(StateDir, fileName); }
    }

    /// <summary>Appends first-person observations to journal\yyyy-MM-dd.md. Thread-safe.</summary>
    public static class Journal
    {
        static readonly object gate = new object();

        /// <summary>Set during headless simulations so test runs don't end up in the real journal.</summary>
        public static bool Muted;

        public static string TodayPath { get { return PathFor(DateTime.Now); } }

        public static string PathFor(DateTime day)
        {
            Home.Init();
            return Path.Combine(Home.JournalDir, day.ToString("yyyy-MM-dd") + ".md");
        }

        public static void Add(string text)
        {
            if (string.IsNullOrEmpty(text) || Muted) return;
            try
            {
                lock (gate)
                {
                    DateTime now = DateTime.Now;
                    string path = PathFor(now);
                    var sb = new StringBuilder();
                    if (!File.Exists(path)) sb.Append("# " + now.ToString("dddd, MMMM d yyyy") + "\r\n\r\n");
                    sb.Append("- " + now.ToString("HH:mm") + " — " + text.Replace("\r", " ").Replace("\n", " ") + "\r\n");
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch (Exception ex) { DebugLog.Write("journal: " + ex.Message); }
        }
    }

    /// <summary>JavaScriptSerializer wrapper plus helpers for loosely-typed JSON (Dictionary&lt;string,object&gt;).</summary>
    public static class Json
    {
        static JavaScriptSerializer Make()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            s.RecursionLimit = 256;
            return s;
        }

        public static string Write(object o) { return Make().Serialize(o); }
        public static T Read<T>(string text) { return Make().Deserialize<T>(text); }

        /// <summary>Parse any JSON. Objects come back as Dictionary&lt;string,object&gt;, arrays as object[].</summary>
        public static object Parse(string text) { return Make().DeserializeObject(text); }

        public static Dictionary<string, object> Obj(string text) { return Parse(text) as Dictionary<string, object>; }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static object[] Arr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new object[0];
            var a = v as object[];
            if (a != null) return a;
            var l = v as System.Collections.ArrayList;
            return l != null ? l.ToArray() : new object[0];
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        public static double Num(Dictionary<string, object> d, string key, double fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            bool b; return bool.TryParse(Convert.ToString(v), out b) ? b : fallback;
        }
    }

    /// <summary>Small rolling debug log in .state\debug.log. Thread-safe, never throws.</summary>
    public static class DebugLog
    {
        static readonly object gate = new object();

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    string path = Home.State("debug.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
