using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bestie
{
    /// <summary>The creature's window (it's also the app's main form), its tray icon and menu,
    /// and the ICreatureHost the assistant talks to.</summary>
    public sealed class CreatureWindow : LayeredWindow, ICreatureHost
    {
        readonly Brain brain;
        readonly Creature c;
        readonly BubbleWindow bubble = new BubbleWindow();
        readonly Timer timer = new Timer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Surface surf;
        double surfScale, lastT, topTimer, trayTimer, leaveAt = -1;
        bool mouseDown, dragging, holdTalking;
        double downTime;
        Point downAt;
        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem napItem, startupItem;
        AssistantModule assistant;
        DiaryForm diary;
        SessionsHandle sessHandle;
        SessionsPanel sessPanel;
        SessionsHotkey sessHotkey;
        readonly List<AiSession> finished = new List<AiSession>();
        double lastAnnounce = -100, nextPeekAt = 6 * 60;
        readonly bool trace = Environment.GetEnvironmentVariable("BESTIE_TRACE") == "1";

        public CreatureWindow(Brain brain)
        {
            this.brain = brain;
            c = new Creature(brain, (uint)Process.GetCurrentProcess().Id);
            Text = brain.Name;
            Cursor = Cursors.Hand;
            timer.Interval = 15;
            timer.Tick += OnTick;
            BuildMenu();
            BuildTray();
            SystemEvents.SessionEnding += (s, e) => SaveAll();
        }

        public Creature Creature { get { return c; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            bubble.Show();
            c.Begin();
            try { BrowserBridge.Start(BrowserBridge.DefaultPort); }
            catch (Exception ex) { DebugLog.Write("bridge failed to start: " + ex.Message); }
            try { assistant = new AssistantModule(this, this); }
            catch (Exception ex) { DebugLog.Write("assistant failed to start: " + ex); }
            c.PeekRequest = () => assistant != null && assistant.Peek();
            try { StartSessions(); }
            catch (Exception ex) { DebugLog.Write("ai sessions failed to start: " + ex); }
            timer.Start();
        }

        void OnTick(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds, dt = now - lastT;
            lastT = now;
            Clock.T = now;
            try { c.Update(dt); }
            catch (Exception ex) { DebugLog.Write("update crashed: " + ex); }

            if (leaveAt >= 0)
            {
                c.Alpha = Math.Max(0, 1 - (now - leaveAt - 2.4) / 0.8);
                if (now - leaveAt > 3.3) { Close(); return; }
            }

            // 60 fps only when things move quickly (held, flying, climbing); 30 fps is plenty for waddling and breathing
            int interval = c.Mode == Mode.Held || c.Mode == Mode.Air || c.Mode == Mode.Wall || c.Mode == Mode.Ceiling || c.Mode == Mode.Egg ? 15 : 33;
            if (timer.Interval != interval) timer.Interval = interval;

            Render();
            bubble.Update(c);
            if (sessHandle != null) sessHandle.Tick(dt);
            AnnounceFinished();
            MaybeBeNosy();
            CheckHoldToTalk();

            topTimer += dt;
            if (topTimer > 3) { topTimer = 0; KeepOnTop(); bubble.KeepOnTop(); }
            trayTimer += dt;
            if (trayTimer > 2 && tray != null)
            {
                trayTimer = 0;
                if (trace)
                    DebugLog.Write("trace " + (int)c.X + "," + (int)c.Y + " " + c.Mode + " " + c.Pose + " act=" + c.CurrentAct + " on=" + c.Support + " says=" + c.BubbleText);
                string doing = c.CurrentAct != null ? Creature.ActPhrase(c.CurrentAct) : c.Pose == Pose.Lie ? "a nap" : "nothing much";
                string tip = brain.Name + " — " + doing;
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
        }

        void Render()
        {
            if (surf == null || Math.Abs(surfScale - c.S) > 0.01)
            {
                if (surf != null) surf.Dispose();
                surf = new Surface((int)(270 * c.S), (int)(260 * c.S));
                surfScale = c.S;
            }
            surf.Clear();
            int wx = (int)Math.Round(c.X - surf.Width / 2.0), wy = (int)Math.Round(c.Y - surf.Height * 0.6);
            if (c.Mode != Mode.Hidden || c.Alpha > 0)
            {
                Renderer.DrawCreature(surf.G, c, (float)(c.X - wx), (float)(c.Y - wy));
                Renderer.DrawParticles(surf.G, c, wx, wy);
            }
            Present(surf, wx, wy, (byte)Math.Max(0, Math.Min(255, 255 * c.Alpha)));
        }

        // ---- mouse ----

        // quick click = pet, click and drag = pick up, click and hold still = talk to it (push-to-talk)
        const double HoldToTalkSeconds = 0.45;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { mouseDown = true; dragging = holdTalking = false; downAt = Control.MousePosition; downTime = Clock.T; c.Freeze(); }
            else if (e.Button == MouseButtons.Right) ShowMenu();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!mouseDown || dragging || holdTalking) return;
            var p = Control.MousePosition;
            if (Math.Abs(p.X - downAt.X) + Math.Abs(p.Y - downAt.Y) > 6) { dragging = true; c.Grab(p.X, p.Y); }
        }

        /// <summary>Called every tick: a press that stays put long enough turns into listening.</summary>
        void CheckHoldToTalk()
        {
            if (!mouseDown || dragging || holdTalking || Clock.T - downTime < HoldToTalkSeconds) return;
            holdTalking = true;
            c.Squash = -0.25;
            c.Emit("what", 1);
            try { if (assistant != null) assistant.StartListening(true); else c.Say("my ears aren't plugged in. (the talking part didn't start.)", false, 0); }
            catch (Exception ex) { DebugLog.Write("hold to talk: " + ex.Message); }
        }

        void EndPress(bool send)
        {
            if (holdTalking)
            {
                try { if (assistant != null && assistant.IsListening) assistant.StopListening(send); } catch (Exception ex) { DebugLog.Write("hold to talk: " + ex.Message); }
            }
            else if (dragging) c.Release();
            else if (send) c.Pet();
            mouseDown = dragging = holdTalking = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            EndPress(true);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            // lost the mouse mid-press (e.g. a dialog popped up): let go, but still send what was said
            if (mouseDown && !Capture) EndPress(holdTalking);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) OpenChat();
        }

        // ---- menu & tray ----

        void BuildMenu()
        {
            menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = new Font("Segoe UI", 9.5f) };
            Add("talk to " + brain.Name + "…", "Ctrl+Alt+B", OpenChat);
            Add("say it out loud", "Ctrl+Alt+V", () => { if (assistant != null) assistant.ToggleVoice(); });
            Add("look at my screen & ask…", null, () => { if (assistant != null) assistant.ShareScreenAndAsk(); });
            menu.Items.Add(new ToolStripSeparator());
            Add("ai sessions", "Ctrl+Alt+A", ToggleSessions);
            Add(brain.Name + "'s diary", null, OpenDiary);
            Add("open " + brain.Name + "'s folder", null, () => Process.Start("explorer.exe", "\"" + Home.Root + "\""));
            Add("come here", null, () => c.Summon(Control.MousePosition.X));
            napItem = Add("take a nap", null, () => { if (c.Pose == Pose.Lie) c.AbortActivity(); else c.DoTrick("nap"); });
            menu.Items.Add(new ToolStripSeparator());
            Add("settings…", null, () => { if (assistant != null) assistant.OpenSettings(); });
            startupItem = Add("live here (start with windows)", null, ToggleStartup);
            Add("say goodbye", null, Goodbye);
            menu.Opening += (s, e) =>
            {
                napItem.Text = c.Pose == Pose.Lie ? "wake up" : "take a nap";
                startupItem.Checked = StartsWithWindows();
            };
        }

        ToolStripMenuItem Add(string text, string keys, Action action)
        {
            var item = new ToolStripMenuItem(text);
            if (keys != null) item.ShortcutKeyDisplayString = keys;
            item.Click += (s, e) => { try { action(); } catch (Exception ex) { DebugLog.Write("menu: " + ex); } };
            menu.Items.Add(item);
            return item;
        }

        void ShowMenu()
        {
            Native.SetForegroundWindowSafe(Handle);
            menu.Show(Control.MousePosition);
        }

        void BuildTray()
        {
            tray = new NotifyIcon { Text = brain.Name, ContextMenuStrip = menu, Visible = true };
            try
            {
                using (var bmp = Portrait.Make(brain, 32, Face.Normal))
                    tray.Icon = Icon.FromHandle(bmp.GetHicon());
            }
            catch (Exception ex) { DebugLog.Write("tray icon: " + ex.Message); tray.Icon = SystemIcons.Application; }
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenChat(); };
        }

        void OpenChat()
        {
            if (assistant != null) assistant.OpenChat();
            else c.Say("my talking part didn't start. check .state\\debug.log?", false, 0);
        }

        void OpenDiary()
        {
            if (diary == null || diary.IsDisposed) diary = new DiaryForm(c);
            diary.Show();
            diary.Activate();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static bool StartsWithWindows()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("Bestie") != null;
        }

        void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue("Bestie") != null)
                {
                    k.DeleteValue("Bestie", false);
                    c.Say("ok. i'll wait for you to wake me.", false, 0);
                }
                else
                {
                    k.SetValue("Bestie", "\"" + Application.ExecutablePath + "\"");
                    c.Say("yay. i live here now. officially.", false, 0);
                    Journal.Add("you said i can live here. i start with windows now.");
                }
            }
        }

        void Goodbye()
        {
            if (leaveAt >= 0) return;
            leaveAt = Clock.T;
            c.Interrupt(c.Farewell(), "bye");
        }

        void SaveAll()
        {
            try { c.SaveSpot(); brain.Save(); } catch (Exception ex) { DebugLog.Write("save: " + ex.Message); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            Journal.Add(e.CloseReason == CloseReason.WindowsShutDown ? "the computer went to sleep. so did i." : "went to sleep. see you.");
            SaveAll();
            try { if (assistant != null) assistant.Dispose(); } catch { }
            try { BrowserBridge.Stop(); } catch { }
            try { AiSessions.Stop(); if (sessHotkey != null) sessHotkey.Dispose(); if (sessPanel != null) sessPanel.Close(); if (sessHandle != null) sessHandle.Close(); } catch { }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            bubble.Close();
            base.OnFormClosing(e);
        }

        /// <summary>Every so often, go see what you're up to (the assistant makes the remark, if it's on).</summary>
        void MaybeBeNosy()
        {
            if (Clock.T < nextPeekAt) return;
            int minutes = 20;
            try { if (assistant != null) minutes = assistant.NosyMinutes; } catch { }
            nextPeekAt = Clock.T + Math.Max(5, minutes) * 60 * Rng.Range(0.7, 1.3);
            if (c.UserAway || c.Mode != Mode.Ground || c.Mood != "idle" || c.Pose == Pose.Lie || c.RitualRunning != null) return;
            c.Interrupt(c.NosyLook(), "nosy");
        }

        // ---- ai sessions sidebar ----

        void StartSessions()
        {
            sessHandle = new SessionsHandle(ToggleSessions);
            sessHandle.Show();
            sessPanel = new SessionsPanel(text => c.Say(text, false, 0));
            sessPanel.SetAccent(Renderer.Hsl(brain.Looks.Hue, 0.55, 0.4, 255));
            AiSessions.Updated += () => OnUi(RefreshSessions);
            AiSessions.Changed += (s, before) => OnUi(() => SessionChanged(s, before));
            AiSessions.Start();
            sessHotkey = new SessionsHotkey(ToggleSessions);
        }

        void OnUi(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        Rectangle PrimaryArea()
        {
            var m = c.World.Mons.FirstOrDefault(o => o.Primary) ?? c.World.Mons.FirstOrDefault();
            return m == null ? Screen.PrimaryScreen.WorkingArea : Rectangle.FromLTRB(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom);
        }

        void RefreshSessions()
        {
            var list = AiSessions.Snapshot();
            sessHandle.SetData(list, PrimaryArea(), (float)c.S, sessPanel.Visible);
            sessPanel.SetSessions(list);
        }

        void ToggleSessions()
        {
            if (sessPanel == null) return;
            if (sessPanel.Visible) { sessPanel.Hide(); return; }
            var list = AiSessions.Snapshot();
            sessPanel.SetSessions(list);
            sessHandle.SetData(list, PrimaryArea(), (float)c.S, true);
            sessPanel.Open(PrimaryArea(), sessHandle.Bounds2);
            string head = AiSessions.Headline();
            c.Say(head ?? Rng.Pick("quiet on the ai floor.", "nobody's working. i checked.", "all the ai tenants are asleep."), false, 0);
        }

        void SessionChanged(AiSession s, string before)
        {
            if (s.Kind == "app" || before != "working" || s.Status == "working") return;
            finished.Add(s);
            Journal.Add(s.Provider + " finished \u201c" + s.Title + "\u201d (" + s.Where + ").");
        }

        /// <summary>Tell the user when AI sessions finish, but not in a flood.</summary>
        void AnnounceFinished()
        {
            if (finished.Count == 0 || Clock.T - lastAnnounce < 6 || c.Mode == Mode.Hidden) return;
            lastAnnounce = Clock.T;
            if (finished.Count == 1)
            {
                var s = finished[0];
                string title = s.Title.Length > 40 ? s.Title.Substring(0, 38) + "\u2026" : s.Title;
                c.Say(s.Provider + " finished \u201c" + title + "\u201d. " + Rng.Pick("your move.", "it's waiting on you.", "tenant's done."), false, 0);
            }
            else
            {
                int claude = finished.Count(x => x.Provider == "claude"), gpt = finished.Count - claude;
                c.Say(finished.Count + " sessions just finished (" + (claude > 0 ? claude + " claude" : "") + (claude > 0 && gpt > 0 ? ", " : "") + (gpt > 0 ? gpt + " chatgpt" : "") + "). your move.", false, 0);
            }
            c.Emit("sparkle", 3);
            finished.Clear();
        }

        // ---- ICreatureHost ----

        public string CreatureName { get { return brain.Name; } }
        public string PersonalitySummary() { return c.Summary(); }

        public string DesktopContext()
        {
            var sb = new StringBuilder();
            sb.AppendLine("local time: " + DateTime.Now.ToString("dddd, MMMM d yyyy, h:mm tt"));
            double idle = Native.IdleSeconds();
            sb.AppendLine("user idle for: " + (idle < 60 ? (int)idle + "s" : (int)(idle / 60) + " min"));
            if (c.NowPlaying != null) sb.AppendLine("now playing (" + c.NowPlaying.Source + "): \u201c" + c.NowPlaying.Title + "\u201d by " + c.NowPlaying.Artist);
            sb.Append(c.World.Describe(25));
            return sb.ToString();
        }

        public void Say(string text, double seconds) { c.Say(text, false, seconds); }
        public void SetMood(string mood) { c.SetMood(mood); }
        public bool DoTrick(string trick) { return c.DoTrick(trick); }
        public string[] TrickNames { get { return Creature.Acts; } }
        public Point CreatureScreenPoint { get { return new Point((int)c.X, (int)c.Y); } }

        public Rectangle CreatureMonitorArea
        {
            get
            {
                var m = c.CurrentMon;
                return m == null ? Screen.PrimaryScreen.WorkingArea : Rectangle.FromLTRB(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); if (surf != null) surf.Dispose(); menu.Dispose(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Renders a still portrait of the creature (tray icon, diary, exe icon).</summary>
    public static class Portrait
    {
        public static Bitmap Make(Brain brain, int size, Face face)
        {
            var c = new Creature(brain, 0);
            c.S = size / 58.0;
            c.Pose = Pose.Stand; c.Face = face; c.LookX = 0.25; c.LookY = 0.1; c.Blush = face == Face.Happy ? 0.6 : 0;
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Renderer.DrawCreature(g, c, size / 2f - 1, size * 0.93f);
            }
            return bmp;
        }
    }
}
