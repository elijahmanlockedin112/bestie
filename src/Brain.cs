using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Bestie
{
    public static class Rng
    {
        static readonly Random r = new Random();
        public static double Next() { return r.NextDouble(); }
        public static double Range(double a, double b) { return a + (b - a) * r.NextDouble(); }
        public static int Int(int a, int bExclusive) { return r.Next(a, bExclusive); }
        public static bool Chance(double p) { return r.NextDouble() < p; }
        public static T Pick<T>(IList<T> list) { return list[r.Next(list.Count)]; }
        public static T Pick<T>(List<T> list) { return list[r.Next(list.Count)]; }
        public static T Pick<T>(params T[] items) { return items[r.Next(items.Length)]; }

        /// <summary>Weighted pick; returns default(T) if all weights are zero.</summary>
        public static T Weighted<T>(IList<T> items, Func<T, double> weight)
        {
            double total = 0;
            foreach (var it in items) total += Math.Max(0, weight(it));
            if (total <= 0) return default(T);
            double x = r.NextDouble() * total;
            foreach (var it in items)
            {
                x -= Math.Max(0, weight(it));
                if (x <= 0) return it;
            }
            return items[items.Count - 1];
        }
    }

    public class Traits { public double Curious, Sleepy, Bold, Chatty, Silly, Cuddly; }
    public class Looks { public double Hue, Sat, Light, Round; public int Eyes, Topper, Pattern; }

    public class AppMemory
    {
        public string Nick, Met;
        public int StoodOn, WalkedOff, Dropped, Naps;
        public bool EdgesKnown;
    }

    public class EdgeMemory { public int Bonks, Boops; public string Found; }
    public class ScreenMemory { public string Nick, Found; public double Seconds; }
    public class Thought { public string At, Text; }

    public class Ritual
    {
        public string Name, When, Born;
        public List<string> Acts = new List<string>();
        public List<string> Places = new List<string>();
        public int Done;
        public double LastDone;   // unix seconds
    }

    /// <summary>Everything the creature remembers. Serialized to .state\brain.json.</summary>
    public class Brain
    {
        public int Version = 1;
        public string Name, Born, LastDecay, LastSpot;
        public double AliveSeconds, LastRitualAt;
        public int Sessions;
        public Traits Traits = new Traits();
        public Looks Looks = new Looks();
        public Dictionary<string, AppMemory> Apps = new Dictionary<string, AppMemory>();
        public Dictionary<string, EdgeMemory> Edges = new Dictionary<string, EdgeMemory>();
        public Dictionary<string, ScreenMemory> Screens = new Dictionary<string, ScreenMemory>();
        public Dictionary<string, Dictionary<string, double>> Habits = new Dictionary<string, Dictionary<string, double>>();
        public Dictionary<string, Dictionary<string, double>> PlaceLove = new Dictionary<string, Dictionary<string, double>>();
        public Dictionary<string, double> Visits = new Dictionary<string, double>();
        public Dictionary<string, double> Artists = new Dictionary<string, double>();   // what you play, and how often
        public Dictionary<string, double> Trigrams = new Dictionary<string, double>();
        public Dictionary<string, string> TrigramPlaces = new Dictionary<string, string>();
        public Dictionary<string, string> TrigramWhen = new Dictionary<string, string>();
        public List<Ritual> Rituals = new List<Ritual>();
        public List<Thought> Thoughts = new List<Thought>();
        public int Pets, Pickups, Throws, Bonks, Falls, Welcomes;
        public double Trust;   // -1 (scared of the cursor) .. 1 (loves it)

        [ScriptIgnore] public string FilePath;

        // ---------- birth & persistence ----------

        public static Brain Hatch()
        {
            var b = new Brain();
            b.Name = Names.Creature();
            b.Born = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            b.LastDecay = DateTime.Now.ToString("yyyy-MM-dd");
            b.Traits = Personality.Traits();
            double[] hues = { 12, 28, 45, 95, 150, 175, 195, 215, 265, 290, 320, 340 };
            b.Looks = new Looks
            {
                Hue = Rng.Pick(hues) + Rng.Range(-8, 8), Sat = Rng.Range(0.45, 0.75), Light = Rng.Range(0.62, 0.74),
                Round = Rng.Range(0, 1), Eyes = Rng.Int(0, 4), Topper = Rng.Int(0, 5), Pattern = Rng.Int(0, 3)
            };
            b.Trust = 0.1 + b.Traits.Cuddly * 0.2;
            return b;
        }

        public static Brain Load(string path)
        {
            Brain b = null;
            if (File.Exists(path))
            {
                try { b = Json.Read<Brain>(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception ex)
                {
                    DebugLog.Write("brain unreadable, keeping a copy: " + ex.Message);
                    try { File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss")); } catch { }
                }
            }
            bool fresh = b == null || string.IsNullOrEmpty(b.Name);
            if (fresh) b = Hatch();
            b.FilePath = path;
            b.FixNulls();
            if (fresh) b.Save();
            return b;
        }

        [ScriptIgnore] public bool IsNewborn { get { return Sessions == 0; } }

        void FixNulls()
        {
            if (Traits == null) Traits = new Traits();
            if (Looks == null) Looks = new Looks();
            if (Apps == null) Apps = new Dictionary<string, AppMemory>();
            if (Edges == null) Edges = new Dictionary<string, EdgeMemory>();
            if (Screens == null) Screens = new Dictionary<string, ScreenMemory>();
            if (Habits == null) Habits = new Dictionary<string, Dictionary<string, double>>();
            if (PlaceLove == null) PlaceLove = new Dictionary<string, Dictionary<string, double>>();
            if (Trigrams == null) Trigrams = new Dictionary<string, double>();
            if (Visits == null) Visits = new Dictionary<string, double>();
            if (Artists == null) Artists = new Dictionary<string, double>();
            if (TrigramPlaces == null) TrigramPlaces = new Dictionary<string, string>();
            if (TrigramWhen == null) TrigramWhen = new Dictionary<string, string>();
            if (Rituals == null) Rituals = new List<Ritual>();
            if (Thoughts == null) Thoughts = new List<Thought>();
            foreach (var r in Rituals) { if (r.Acts == null) r.Acts = new List<string>(); if (r.Places == null) r.Places = new List<string>(); }
        }

        public void Save()
        {
            if (FilePath == null) return;
            try
            {
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, Json.Write(this), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { DebugLog.Write("brain save failed: " + ex.Message); }
        }

        /// <summary>Habits fade a little each day, so routines can drift ("it's going through a phase").</summary>
        public void DailyDecay()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (LastDecay == today) return;
            DateTime last;
            int days = DateTime.TryParse(LastDecay, out last) ? Math.Max(1, Math.Min(10, (int)(DateTime.Now.Date - last.Date).TotalDays)) : 1;
            double f = Math.Pow(0.85, days);
            foreach (var d in Habits.Values) Scale(d, f);
            foreach (var d in PlaceLove.Values) Scale(d, f);
            Scale(Trigrams, f);
            LastDecay = today;
        }

        static void Scale(Dictionary<string, double> d, double f)
        {
            foreach (var k in d.Keys.ToList())
            {
                d[k] *= f;
                if (d[k] < 0.05) d.Remove(k);
            }
        }

        // ---------- habits: a Pólya urn, so early coincidences harden into routines ----------

        public static string TimeBucket(DateTime t)
        {
            int h = t.Hour;
            if (h < 5) return "wee hours";
            if (h < 8) return "dawn";
            if (h < 12) return "morning";
            if (h < 17) return "afternoon";
            if (h < 21) return "evening";
            return "night";
        }

        static double Get(Dictionary<string, Dictionary<string, double>> m, string a, string b)
        {
            Dictionary<string, double> d; double v;
            return m.TryGetValue(a, out d) && d.TryGetValue(b, out v) ? v : 0;
        }

        static void Add(Dictionary<string, Dictionary<string, double>> m, string a, string b, double amt)
        {
            Dictionary<string, double> d;
            if (!m.TryGetValue(a, out d)) m[a] = d = new Dictionary<string, double>();
            double v; d.TryGetValue(b, out v); d[b] = v + amt;
        }

        public double Habit(string time, string prev, string act)
        {
            return Get(Habits, time + "|" + prev, act) + 0.5 * Get(Habits, "*|" + prev, act);
        }

        public void Reinforce(string time, string prev, string act)
        {
            Add(Habits, time + "|" + prev, act, 1);
            Add(Habits, "*|" + prev, act, 1);
        }

        public double Love(string act, string place) { return Get(PlaceLove, act, place); }
        public void AddLove(string act, string place, double amt) { if (place != null) Add(PlaceLove, act, place, amt); }

        public string FavoritePlace(string act)
        {
            Dictionary<string, double> d;
            if (!PlaceLove.TryGetValue(act, out d) || d.Count == 0) return null;
            return d.OrderByDescending(kv => kv.Value).First().Key;
        }

        /// <summary>Strongest "after X, i usually do Y" habits, for the diary and the assistant.</summary>
        public List<string[]> StrongHabits(int n)
        {
            var list = new List<string[]>();
            foreach (var ctx in Habits)
            {
                if (!ctx.Key.StartsWith("*|")) continue;
                string prev = ctx.Key.Substring(2);
                double total = ctx.Value.Values.Sum();
                if (total < 3) continue;
                var top = ctx.Value.OrderByDescending(kv => kv.Value).First();
                if (top.Value / total >= 0.4) list.Add(new[] { prev, top.Key, (top.Value / total).ToString("0.00"), top.Value.ToString("0") });
            }
            return list.OrderByDescending(x => double.Parse(x[3])).Take(n).ToList();
        }

        // ---------- rituals ----------

        /// <summary>Count an activity trigram; returns a newly crystallized ritual, if any.</summary>
        public Ritual NoteTrigram(string a, string b, string c, string pa, string pb, string pc, string time, double now)
        {
            // a tradition needs at least two different things that aren't just strolling or sitting around
            var acts = new[] { a, b, c };
            if (acts.Where(x => x != "wander" && x != "sit").Distinct().Count() < 2) return null;
            string key = a + ">" + b + ">" + c;
            double v; Trigrams.TryGetValue(key, out v);
            Trigrams[key] = v + 1;
            TrigramPlaces[key] = pa + "|" + pb + "|" + pc;
            TrigramWhen[key] = time;
            if (v + 1 < 4 || Rituals.Count >= 7 || now - LastRitualAt < 30 * 60) return null;
            if (Rituals.Any(r => string.Join(">", r.Acts) == key)) return null;
            LastRitualAt = now;
            var rit = new Ritual { When = time, Born = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            rit.Acts.AddRange(new[] { a, b, c });
            rit.Places.AddRange(TrigramPlaces[key].Split('|'));
            rit.Name = Names.Ritual(rit.Acts, time, Rituals.Select(r => r.Name));
            Rituals.Add(rit);
            return rit;
        }

        // ---------- bookkeeping ----------

        public AppMemory App(string app)
        {
            AppMemory m;
            if (!Apps.TryGetValue(app, out m))
            {
                m = new AppMemory { Met = DateTime.Now.ToString("yyyy-MM-dd"), Nick = Names.ForApp(app, Apps.Values.Select(x => x.Nick)) };
                Apps[app] = m;
            }
            return m;
        }

        public string Nick(string app)
        {
            if (app == null) return "the floor";
            return App(app).Nick;
        }

        public EdgeMemory Edge(string key)
        {
            EdgeMemory e; return Edges.TryGetValue(key, out e) ? e : null;
        }

        public void Remember(string text)
        {
            Thoughts.Add(new Thought { At = DateTime.Now.ToString("MMM d, HH:mm"), Text = text });
            while (Thoughts.Count > 40) Thoughts.RemoveAt(0);
        }

        public string Age()
        {
            DateTime born;
            if (!DateTime.TryParse(Born, out born)) return "a while";
            var span = DateTime.Now - born;
            if (span.TotalMinutes < 90) return Plural((int)Math.Max(1, span.TotalMinutes), "minute");
            if (span.TotalHours < 36) return Plural((int)span.TotalHours, "hour");
            return Plural((int)span.TotalDays, "day");
        }

        static string Plural(int n, string unit) { return n + " " + unit + (n == 1 ? "" : "s"); }

        public static string Describe(double v, string low, string mid, string high)
        {
            return v < 0.35 ? low : v < 0.7 ? mid : high;
        }
    }

    /// <summary>Silly name generators.</summary>
    public static class Names
    {
        static readonly string[] Curated = { "Pib", "Gorb", "Mungo", "Tuvi", "Blep", "Squidge", "Doug", "Noodle", "Bean", "Wimble",
            "Gloop", "Fennel", "Mothball", "Plum", "Grub", "Toast", "Pudge", "Nib", "Kevin", "Soup", "Button", "Crumb" };
        static readonly string[] Starts = { "b", "bl", "br", "d", "dr", "f", "fl", "g", "gl", "gr", "j", "k", "l", "m", "n", "p", "pl",
            "s", "sn", "sp", "t", "tr", "v", "w", "z", "sh", "ch", "m", "b", "p", "bl", "gl" };
        static readonly string[] Vowels = { "a", "e", "i", "o", "u", "oo", "ee", "ui", "ou", "a", "o", "u" };
        static readonly string[] Ends = { "b", "bb", "p", "m", "mp", "n", "g", "gg", "t", "sh", "k", "x", "z", "rb", "lb", "nk", "mble", "bo", "by", "ly", "sk", "p", "m" };

        public static string Creature()
        {
            if (Rng.Chance(0.3)) return Rng.Pick(Curated);
            string s = Rng.Pick(Starts) + Rng.Pick(Vowels) + Rng.Pick(Ends);
            if (Rng.Chance(0.3)) s += Rng.Pick("o", "y", "le", "et", "us", "a");
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]> {
            { "browser", new[] { "the Window Full of Windows", "the Tab Shelf", "the Internet Plank", "the Big Glowing Slab" } },
            { "files", new[] { "the Folder Cave", "the Drawer", "the File Box" } },
            { "code", new[] { "the Glowing Words", "the Letter Soup", "the Code Plank", "the Bracket Shelf" } },
            { "terminal", new[] { "the Dark Box", "the Shouting Rectangle", "the Command Ledge" } },
            { "music", new[] { "the Hum Shelf", "the Music Plank", "the Noise Ledge" } },
            { "chat", new[] { "the Chatter Shelf", "the Gossip Plank", "the Talky Ledge" } },
            { "notepad", new[] { "the Little Paper", "the Thin Paper" } },
            { "office", new[] { "the Serious Plank", "the Paperwork Shelf" } },
            { "sheet", new[] { "the Grid Shelf", "the Number Waffle" } },
            { "mail", new[] { "the Letter Ledge", "the Envelope Shelf" } },
            { "games", new[] { "the Game Shelf", "the Fun Crate" } },
            { "video", new[] { "the Movie Ledge", "the Moving Picture Plank" } },
            { "claude", new[] { "my Cousin", "the Clever Plank" } },
            { "art", new[] { "the Painting Plank", "the Colors Shelf" } },
            { "settings", new[] { "the Knob Shelf", "the Switch Box" } },
            { "calc", new[] { "the Number Shelf" } },
            { "taskmgr", new[] { "the Worry Box" } },
        };

        static string Kind(string app)
        {
            if (app == null) return null;
            string[][] map = {
                new[] { "browser", "chrome", "msedge", "firefox", "brave", "opera", "arc", "vivaldi", "iexplore", "zen", "floorp", "librewolf" },
                new[] { "files", "explorer" },
                new[] { "code", "code - insiders", "devenv", "cursor", "idea64", "pycharm64", "rider64", "webstorm64", "notepad++", "sublime_text", "zed", "windsurf" },
                new[] { "terminal", "windowsterminal", "cmd", "powershell", "pwsh", "wt", "conhost", "wezterm-gui", "alacritty", "mintty" },
                new[] { "music", "spotify", "music.ui", "itunes", "foobar2000", "applemusic" },
                new[] { "chat", "discord", "slack", "teams", "ms-teams", "telegram", "whatsapp", "signal", "zoom", "messenger" },
                new[] { "notepad", "notepad" },
                new[] { "office", "winword", "powerpnt", "onenote", "acrobat", "acrord32", "obsidian", "notion" },
                new[] { "sheet", "excel" },
                new[] { "mail", "outlook", "olk", "thunderbird", "hxoutlook" },
                new[] { "games", "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "gog galaxy" },
                new[] { "video", "vlc", "mpc-hc64", "mpv", "potplayermini64", "obs64" },
                new[] { "claude", "claude" },
                new[] { "art", "photoshop", "figma", "gimp", "krita", "clipstudiopaint", "paint.net", "mspaint", "illustrator" },
                new[] { "settings", "app:settings", "systemsettings" },
                new[] { "calc", "app:calculator", "calculatorapp" },
                new[] { "taskmgr", "taskmgr" },
            };
            foreach (var row in map)
                for (int i = 1; i < row.Length; i++) if (app == row[i]) return row[0];
            return null;
        }

        static readonly string[] Adjectives = { "Wobbly", "Humming", "Shiny", "Crinkly", "Sleepy", "Grumpy", "Long", "Flat", "Warm", "Tall",
            "Suspicious", "Friendly", "Crunchy", "Soft", "Loud", "Quiet", "Mysterious", "Tiny", "Sturdy", "Bouncy", "Polite", "Damp" };
        static readonly string[] Nouns = { "Plank", "Shelf", "Ledge", "Slab", "Toast", "Brick", "Bridge", "Roof", "Table", "Board",
            "Cracker", "Biscuit", "Balcony", "Wafer", "Porch", "Bench" };

        public static string ForApp(string app, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(taken.Where(t => t != null));
            string kind = Kind(app);
            if (kind != null)
            {
                var free = Known[kind].Where(n => !used.Contains(n)).ToList();
                if (free.Count > 0) return Rng.Pick(free);
            }
            for (int i = 0; i < 40; i++)
            {
                string n = "the " + Rng.Pick(Adjectives) + " " + Rng.Pick(Nouns);
                if (!used.Contains(n)) return n;
            }
            return "the Other " + Rng.Pick(Nouns) + " " + Rng.Int(2, 99);
        }

        public static string Screen(Mon m, Mon home, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(taken.Where(t => t != null));
            var options = new List<string>();
            if (home == null || m == home) options.AddRange(new[] { "the Homeland", "the Old Country", "the Main Place" });
            else if (m.Bounds.Right <= home.Bounds.Left + 4) options.AddRange(new[] { "the Left Lands", "the Western Screen", "Over There" });
            else if (m.Bounds.Left >= home.Bounds.Right - 4) options.AddRange(new[] { "the Right Realm", "the Far East", "the Other Side" });
            else if (m.Bounds.Bottom <= home.Bounds.Top + 4) options.AddRange(new[] { "the Up There", "the Attic" });
            else options.AddRange(new[] { "the Basement", "the Down Under" });
            options.AddRange(new[] { "the Other Place", "the Sequel", "the Annex" });
            foreach (var o in options) if (!used.Contains(o)) return o;
            return "Screen " + (m.Index + 1);
        }

        static readonly Dictionary<string, string[]> ActNouns = new Dictionary<string, string[]> {
            { "dance", new[] { "Wiggle", "Boogie", "Shimmy", "Jig" } },
            { "nap", new[] { "Flop", "Snooze", "Siesta" } },
            { "sniff", new[] { "Sniff-About", "Snuffle", "Nose Tour" } },
            { "patrol", new[] { "Rounds", "Patrol", "Inspection" } },
            { "climb", new[] { "Ascent", "Wall Thing", "Big Climb" } },
            { "spin", new[] { "Twirl", "Spin Cycle", "Whirl" } },
            { "sing", new[] { "Recital", "Hum", "Concert" } },
            { "peek", new[] { "Peek", "Lookout" } },
            { "sit", new[] { "Ponder", "Big Think", "Contemplation" } },
            { "exercise", new[] { "Workout", "Stretches", "Calisthenics" } },
            { "count", new[] { "Census", "Headcount", "Tally" } },
            { "boop", new[] { "Boop", "Edge Check" } },
            { "sunbathe", new[] { "Sunbath", "Bask" } },
            { "wander", new[] { "Stroll", "Mosey" } },
            { "explore", new[] { "Expedition", "Survey" } },
            { "visit", new[] { "Visit", "Hello Walk" } },
        };

        static readonly Dictionary<string, string> TimeWords = new Dictionary<string, string> {
            { "wee hours", "Wee-Hours" }, { "dawn", "Dawn" }, { "morning", "Morning" }, { "afternoon", "Afternoon" },
            { "evening", "Evening" }, { "night", "Midnight" }
        };

        public static string Ritual(List<string> acts, string time, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(taken);
            var boring = new HashSet<string> { "wander", "sit", "explore" };
            string star = acts.FirstOrDefault(a => !boring.Contains(a)) ?? acts[0];
            string[] nouns; if (!ActNouns.TryGetValue(star, out nouns)) nouns = new[] { "Thing" };
            string tw; if (!TimeWords.TryGetValue(time ?? "", out tw)) tw = "Daily";
            for (int i = 0; i < 20; i++)
            {
                string prefix = Rng.Chance(0.35) ? Rng.Pick("Great ", "Little ", "Secret ", "Famous ", "Official ") : "";
                string n = "the " + prefix + tw + " " + Rng.Pick(nouns);
                if (!used.Contains(n)) return n;
            }
            return "the " + tw + " " + Rng.Pick(nouns) + " No. " + Rng.Int(2, 9);
        }
    }
}
