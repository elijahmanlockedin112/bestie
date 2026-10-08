using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Bestie
{
    static class Program
    {
        /// <summary>Everything Bestie starts (project launchers, claude, powershell) inherits its environment. When it was
        /// started from a tool session (a Claude Code shell, a terminal) that environment carries the tool's settings:
        /// NoDefaultCurrentDirectoryInExePath stops batch files from running a sibling script by name (their launchers
        /// die instantly), and CLAUDECODE makes claude think it's nested. So: the user's own Windows environment.</summary>
        static void CleanEnvironment()
        {
            try
            {
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    string name = (string)e.Key;
                    if (name.Equals("NoDefaultCurrentDirectoryInExePath", StringComparison.OrdinalIgnoreCase) || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) || name.Equals("ANTHROPIC_BASE_URL", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("GIT_EDITOR", StringComparison.OrdinalIgnoreCase) || name.Equals("GIT_TERMINAL_PROMPT", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("GIT_ASKPASS", StringComparison.OrdinalIgnoreCase))
                    {
                        // only if it isn't one of their own saved variables
                        if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) == null && Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) == null)
                            Environment.SetEnvironmentVariable(name, null);
                    }
                }
                // PATH as Windows would give it (a tool session prepends its own folders)
                string machine = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
                string user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
                if (machine.Length > 0) Environment.SetEnvironmentVariable("Path", Environment.ExpandEnvironmentVariables(machine + ";" + user));
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            CleanEnvironment();
            Native.MakeDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Home.Init();

            // developer tools
            if (args.Length >= 2 && args[0] == "--sheet") { Tools.Sheet(args[1]); return 0; }
            if (args.Length >= 3 && args[0] == "--simulate") { Tools.Simulate(double.Parse(args[1]), args[2]); return 0; }
            if (args.Length >= 2 && args[0] == "--icon") { Tools.WriteIcon(args[1]); return 0; }
            if (args.Length >= 2 && args[0] == "--diary") { Tools.DiaryImage(args[1]); return 0; }
            if (args.Length >= 2 && args[0] == "--widgets") { Tools.WidgetsImage(args[1]); return 0; }
            if (args.Length >= 2 && args[0] == "--connect-calendar")
            {
                // same as the menu's "let me see my calendar", for scripts: Bestie.exe --connect-calendar <link> [result.txt]
                string msg;
                bool ok = IcsCalendar.Connect(args[1], out msg);
                if (args.Length >= 3) File.WriteAllText(args[2], (ok ? "ok: " : "failed: ") + msg);
                return ok ? 0 : 1;
            }
            if (args.Length >= 2 && args[0] == "--sessions")
            {
                AiSessions.Start();
                Thread.Sleep(2500);
                File.WriteAllText(args[1], AiSessions.Describe(40) + "\r\n" + (AiSessions.Headline() ?? "(nothing working)"));
                if (args.Length >= 3) Tools.PanelImage(args[2]);
                return 0;
            }

            // "Bestie.exe --quit" asks a running Bestie to say goodbye properly (it saves on the way out)
            if (args.Length >= 1 && args[0] == "--quit")
            {
                EventWaitHandle quit;
                if (EventWaitHandle.TryOpenExisting(@"Local\Bestie.Quit", out quit)) { quit.Set(); quit.Dispose(); }
                return 0;
            }

            bool fresh;
            using (var mutex = new Mutex(true, @"Local\Bestie.Creature", out fresh))
            {
                if (!fresh)
                {
                    // already living here: launching it again (say, from the taskbar) calls it over instead
                    EventWaitHandle hello;
                    if (EventWaitHandle.TryOpenExisting(@"Local\Bestie.Hello", out hello)) { hello.Set(); hello.Dispose(); }
                    return 0;
                }
                Application.ThreadException += (s, e) => DebugLog.Write("ui thread: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => DebugLog.Write("fatal: " + e.ExceptionObject);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                var brain = Brain.Load(Home.State("brain.json"));
                brain.Traits = Personality.Traits();   // the coded-in character, not the dice it hatched with
                var window = new CreatureWindow(brain);
                var quitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Bestie.Quit");
                ThreadPool.RegisterWaitForSingleObject(quitSignal, (s, timedOut) =>
                {
                    try { window.BeginInvoke(new Action(window.Close)); } catch { }
                }, null, -1, true);
                var helloSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Bestie.Hello");
                ThreadPool.RegisterWaitForSingleObject(helloSignal, (s, timedOut) =>
                {
                    try { window.BeginInvoke(new Action(window.ComeSayHi)); } catch { }
                }, null, -1, false);
                ThreadPool.QueueUserWorkItem(_ => StartMenu.Ensure());
                Application.Run(window);
                GC.KeepAlive(quitSignal);   // or the quit event could be collected (and never fire) while we run
                GC.KeepAlive(helloSignal);
                brain.Save();
                // everything is saved; don't let a straggling background thread (audio, sockets) keep the process around
                Environment.Exit(0);
            }
            return 0;
        }
    }

    /// <summary>Keeps a Start menu shortcut pointing at this Bestie.exe, so it's easy to find and to pin
    /// (Windows doesn't let apps pin themselves: Start, type "bestie", right-click, Pin to taskbar).</summary>
    static class StartMenu
    {
        public static string LinkPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Bestie.lnk"); } }

        public static void Ensure()
        {
            try
            {
                string exe = Application.ExecutablePath;
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object shell = Activator.CreateInstance(t);
                try
                {
                    bool had = File.Exists(LinkPath);
                    object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { LinkPath });
                    Type lt = link.GetType();
                    string current = lt.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                    if (had && string.Equals(current, exe, StringComparison.OrdinalIgnoreCase)) return;
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe) });
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "a little weirdo who lives on your screen" });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
                    Marshal.ReleaseComObject(link);
                    DebugLog.Write("start menu: " + (had ? "pointed the Bestie shortcut at " : "added a Bestie shortcut for ") + exe);
                }
                finally { Marshal.ReleaseComObject(shell); }
            }
            catch (Exception ex) { DebugLog.Write("start menu shortcut: " + ex.Message); }
        }
    }

    /// <summary>Things for poking at the creature without living with it.</summary>
    static class Tools
    {
        /// <summary>Render a sheet of poses and looks to a PNG, to check the art.</summary>
        public static void Sheet(string path)
        {
            Clock.T = 1.3;
            var brain = Brain.Hatch();
            brain.Looks.Pattern = 1; brain.Looks.Topper = 0; brain.Looks.Eyes = 0;
            var c = new Creature(brain, 0) { S = 2 };
            var poses = new[] {
                new object[] { "stand", Pose.Stand, Face.Normal }, new object[] { "walk", Pose.Walk, Face.Normal },
                new object[] { "happy", Pose.Stand, Face.Happy }, new object[] { "dance", Pose.Dance, Face.Happy },
                new object[] { "sit", Pose.Sit, Face.Content }, new object[] { "nap", Pose.Lie, Face.Sleep },
                new object[] { "sniff", Pose.Sniff, Face.Normal }, new object[] { "peek", Pose.Peek, Face.Normal },
                new object[] { "held", Pose.Held, Face.Surprised }, new object[] { "dizzy", Pose.Stand, Face.Dizzy },
                new object[] { "bonk", Pose.Stand, Face.Squint }, new object[] { "sunbathe", Pose.Sunbathe, Face.Content },
                new object[] { "sing", Pose.Sing, Face.Happy }, new object[] { "wave", Pose.Wave, Face.Happy },
                new object[] { "listening", Pose.Stand, Face.Surprised }, new object[] { "egg", Pose.Egg, Face.Normal },
            };
            int cell = 150, cols = 8, rows = (poses.Length + cols - 1) / cols + 2;
            using (var bmp = new Bitmap(cell * cols, cell * rows))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(238, 240, 245));
                var font = new Font("Segoe UI", 10);
                for (int i = 0; i < poses.Length; i++)
                {
                    float x = (i % cols) * cell, y = (i / cols) * cell;
                    c.Pose = (Pose)poses[i][1]; c.Face = (Face)poses[i][2];
                    c.Sunglasses = c.Pose == Pose.Sunbathe; c.WalkPhase = 1.1; c.LookX = 0.6; c.LookY = 0.2; c.Blush = c.Face == Face.Happy ? 1 : 0;
                    c.Talk = (string)poses[i][0] == "sing" ? 0.8 : 0; c.EggCrack = 2; c.EggWobble = 0.4;
                    c.Rot = 0; c.Squash = 0; c.Halo = (string)poses[i][0] == "listening" ? 1 : 0;
                    Renderer.DrawCreature(g, c, x + cell / 2, y + cell - 25);
                    g.DrawString((string)poses[i][0], font, Brushes.DimGray, x + 6, y + 4);
                }
                // climbing orientations
                c.Pose = Pose.Climb; c.Face = Face.Normal;
                c.Rot = 90; Renderer.DrawCreature(g, c, 2 * cell + 20, 2 * cell + 75); g.DrawString("left wall", font, Brushes.DimGray, 2 * cell + 6, 2 * cell + 4);
                c.Rot = -90; Renderer.DrawCreature(g, c, 4 * cell - 20, 2 * cell + 75); g.DrawString("right wall", font, Brushes.DimGray, 3 * cell + 6, 2 * cell + 4);
                c.Rot = 180; Renderer.DrawCreature(g, c, 4 * cell + 75, 2 * cell + 20); g.DrawString("ceiling", font, Brushes.DimGray, 4 * cell + 6, 2 * cell + 40);
                c.Rot = 0;
                // a row of other random weirdos
                for (int i = 0; i < 8; i++)
                {
                    var other = new Creature(Brain.Hatch(), 0) { S = 1.6 };
                    other.Pose = Pose.Stand; other.LookX = 0.4;
                    Renderer.DrawCreature(g, other, i * cell + cell / 2, (rows - 1) * cell + cell - 30);
                    g.DrawString(other.Brain.Name, font, Brushes.DimGray, i * cell + 6, (rows - 1) * cell + 4);
                }
                // particles and bubbles
                c.X = 6 * cell; c.Y = 2 * cell + 120; c.Particles.Clear();
                foreach (var k in new[] { "z", "heart", "note", "star", "sparkle", "bang" }) c.Emit(k, 1);
                foreach (var p in c.Particles) { p.Life = p.MaxLife * 0.8; p.Y -= 20; }
                for (int i = 0; i < c.Particles.Count; i++) c.Particles[i].X = 5 * cell + 20 + i * 45;
                Renderer.DrawParticles(g, c, 0, 0);
                SizeF ts;
                var size = Renderer.MeasureBubble(g, "the Tab Shelf smells like tabs.", 1.4f, out ts);
                Renderer.DrawBubble(g, "the Tab Shelf smells like tabs.", false, 5 * cell + 10, 2 * cell + 10, size.Width, size.Height - 12 * 1.4f, 5 * cell + 60, false, 1.4f, 255);
                bmp.Save(path, ImageFormat.Png);
            }
        }

        /// <summary>Run the creature's mind at full speed, headless, against the real desktop.</summary>
        public static void Simulate(double minutes, string outPath)
        {
            Clock.Simulated = true;
            Journal.Muted = true;
            var brain = Brain.Hatch();
            brain.Sessions = 1;   // skip the egg
            using (var w = new StreamWriter(outPath))
            {
                Creature.SimOut = w;
                var c = new Creature(brain, (uint)Process.GetCurrentProcess().Id);
                w.WriteLine("simulating " + brain.Name + " for " + minutes + " minutes. traits: " + c.TraitWords());
                c.Begin();
                double dt = 0.05;
                while (Clock.T < minutes * 60)
                {
                    Clock.T += dt;
                    c.Update(dt);
                }
                w.WriteLine();
                w.WriteLine("---- summary ----");
                w.WriteLine(c.Summary());
                w.WriteLine("trigrams: " + string.Join(", ", brain.Trigrams.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key + "=" + kv.Value.ToString("0.0"))));
                w.WriteLine("edges: " + string.Join(", ", brain.Edges.Keys));
                w.WriteLine("apps: " + string.Join(", ", brain.Apps.Select(kv => kv.Key + "=" + kv.Value.Nick)));
            }
        }

        /// <summary>Render the diary of a simulated creature (60 busy minutes) to a PNG, to check the layout.</summary>
        public static void DiaryImage(string path)
        {
            Clock.Simulated = true;
            Journal.Muted = true;
            var brain = Brain.Hatch();
            brain.Sessions = 1;
            var c = new Creature(brain, (uint)Process.GetCurrentProcess().Id);
            c.Begin();
            while (Clock.T < 240 * 60) { Clock.T += 0.05; c.Update(0.05); }
            brain.Pets = 14; brain.Throws = 1; brain.Pickups = 5;
            var page = new DiaryPage(c, 1);
            page.Relayout(560);
            using (var bmp = new Bitmap(page.Width, page.Height))
            {
                page.DrawToBitmap(bmp, new Rectangle(0, 0, page.Width, page.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        public static void PanelImage(string path)
        {
            var panel = new SessionsPanel(s => { });
            panel.SetSessions(AiSessions.Snapshot());
            panel.Open(new Rectangle(0, 0, 1920, 1080), new Rectangle(1882, 280, 38, 128));
            Application.DoEvents();
            using (var bmp = new Bitmap(panel.Width, panel.Height))
            {
                panel.DrawToBitmap(bmp, new Rectangle(0, 0, panel.Width, panel.Height));
                bmp.Save(path, ImageFormat.Png);
            }
            panel.Close();
        }

        /// <summary>Render every widget (with whatever data is around) side by side into a PNG.</summary>
        public static void WidgetsImage(string path)
        {
            var brain = Brain.Load(Home.State("brain.json"));
            brain.FilePath = null;   // look, don't touch
            var c = new Creature(brain, 0);
            c.NowPlaying = new Song { Title = "End of Beginning", Artist = "Djo", Source = "spotify" };
            var host = new Form();
            host.CreateControl();
            WidgetManager.Init(host, c, () => null);
            var forms = new WidgetForm[] {
                new TodoWidget(new WidgetState { Id = "todo", Kind = "todo" }),
                new CalendarWidget(new WidgetState { Id = "calendar", Kind = "calendar" }),
                new MusicWidget(new WidgetState { Id = "music", Kind = "music" }),
                new NoteWidget(new WidgetState { Id = "note-99", Kind = "note", Color = 0 }),
                new TimeBlockWidget(new WidgetState { Id = "timeblocks", Kind = "timeblocks" }) };
            int gap = 20, width = gap, height = 0;
            foreach (var f in forms) { f.CreateControl(); width += f.Width + gap; height = Math.Max(height, f.Height); }
            using (var bmp = new Bitmap(width, height + gap * 2))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(58, 62, 74));
                int x = gap;
                foreach (var f in forms)
                {
                    using (var one = new Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(one, new Rectangle(0, 0, f.Width, f.Height));
                        g.DrawImage(one, x, gap);
                    }
                    x += f.Width + gap;
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        public static void WriteIcon(string path)
        {
            var brain = Brain.Hatch();
            brain.Looks = new Looks { Hue = 160, Sat = 0.55, Light = 0.68, Round = 0.5, Eyes = 0, Topper = 0, Pattern = 1 };
            using (var fs = File.Create(path))
            {
                int[] sizes = { 16, 32, 48, 256 };
                var pngs = sizes.Select(sz =>
                {
                    using (var bmp = Portrait.Make(brain, sz, Face.Happy))
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
                }).ToList();
                var bw = new BinaryWriter(fs);
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                    bw.Write(pngs[i].Length); bw.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) bw.Write(p);
                bw.Flush();
            }
        }
    }
}
