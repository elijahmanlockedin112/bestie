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
        ToolStripMenuItem napItem, startupItem, calendarItem, forgetCalendarsItem, brainDumpItem;
        double calendarCheck = 20, blockCheck;
        int calendarChecking;
        AssistantModule assistant;
        DiaryForm diary;
        SessionsHandle sessHandle;
        SessionsPanel sessPanel;
        SessionsHotkey sessHotkey;
        readonly List<AiSession> finished = new List<AiSession>();
        // sessions that just finished a turn, waiting to see if they really stopped (id -> when)
        readonly Dictionary<string, KeyValuePair<AiSession, double>> maybeFinished = new Dictionary<string, KeyValuePair<AiSession, double>>();
        const double FinishSettleSeconds = 20;
        double lastAnnounce = -100, nextPeekAt = 6 * 60;
        readonly bool trace = Environment.GetEnvironmentVariable("BESTIE_TRACE") == "1";
        WakeWord wake;
        double wakeCheck, wakeRetryAt;
        readonly AudioDucker ducker = new AudioDucker();
        WisprWatcher wispr;
        WisprCapture wisprCapture;
        double listenCheck;

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
            Watchdog.Start(this);
            bubble.Show();
            Pointer.Ignore = CreatureBox;
            c.Begin();
            try { BrowserBridge.Start(BrowserBridge.DefaultPort); }
            catch (Exception ex) { DebugLog.Write("bridge failed to start: " + ex.Message); }
            try { assistant = new AssistantModule(this, this); }
            catch (Exception ex) { DebugLog.Write("assistant failed to start: " + ex); }
            c.PeekRequest = () => assistant != null && assistant.Peek();
            try { StartSessions(); }
            catch (Exception ex) { DebugLog.Write("ai sessions failed to start: " + ex); }
            try { StartTimeBlocks(); }
            catch (Exception ex) { DebugLog.Write("time blocks failed to start: " + ex); }
            try { StartWispr(); }
            catch (Exception ex) { DebugLog.Write("wispr failed to start: " + ex); }
            try
            {
                WidgetManager.ConnectCalendar = ConnectCalendar;
                WidgetManager.Presented += r =>
                {
                    // the creature points at the widget that just came forward
                    double cx = r.Left + r.Width / 2.0, cy = r.Top + r.Height / 2.0;
                    var wm = c.World.MonAt(cx, cy);
                    var cm = c.CurrentMon;
                    string spot = "";
                    if (wm != null)
                        spot = (cy < (wm.Work.Top + wm.Work.Bottom) / 2.0 ? "top" : "bottom") + " " + (cx < (wm.Work.Left + wm.Work.Right) / 2.0 ? "left" : "right");
                    string line = wm != null && cm != null && wm.Index != cm.Index ? "it's on your other screen, " + spot + "." : Rng.Pick("there it is. ", "there. ", "put it ") + spot + ".";
                    c.PointAt(cx, cy, line);
                };
                WidgetManager.Init(this, c, () => assistant);
                Todos.Completed += item => OnUi(() =>
                {
                    c.Say(Rng.Pick("one less violation.", "checked off. the building thanks you.", "done! 7 out of 7.", "“" + (item.Text.Length > 30 ? item.Text.Substring(0, 28) + "…" : item.Text) + "”: handled."), false, 0);
                    c.Emit("sparkle", 4);
                    c.Squash = -0.3;
                });
            }
            catch (Exception ex) { DebugLog.Write("widgets failed to start: " + ex); }
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
            Pointer.Tick();
            if (sessHandle != null) sessHandle.Tick(dt);
            SettleFinished();
            AnnounceFinished();
            MaybeBeNosy();
            CheckHoldToTalk();
            CheckCalendarHeadsUps(dt);
            blockCheck -= dt;
            if (blockCheck <= 0)
            {
                blockCheck = 0.4;
                TimeBlocks.Tick();
                if (TimeBlocks.BreakLocked && !BreakOverlay.Active && leaveAt < 0) BreakOverlay.Show();
                BreakOverlay.Tick();
                if (BreakOverlay.Active) { KeepOnTop(); bubble.KeepOnTop(); }   // it keeps you company on the break screen
            }
            ducker.Tick(dt);
            listenCheck -= dt;
            if (listenCheck <= 0)
            {
                listenCheck = 0.3;
                // cup an ear while you dictate anywhere with wispr
                bool along = false;
                try { along = brain.WisprListen && wispr != null && !(wisprCapture != null && wisprCapture.Active) && Wispr.Running && Wispr.IsDictating(); } catch { }
                c.ListenAlong = along;
                // dictating into something else right after it spoke: that's not an answer for it
                if (along && assistant != null) assistant.CancelFollowUp();
            }
            wakeCheck -= dt;
            if (wakeCheck <= 0) { wakeCheck = 5; SyncWakeWord(); }

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
            // talking through wispr moves focus to its little box, so the mouse-up may never reach us: watch the button itself
            if (holdTalking && wisprCapture != null && wisprCapture.Active && (Control.MouseButtons & MouseButtons.Left) == 0) { EndPress(true); return; }
            if (!mouseDown || dragging || holdTalking || Clock.T - downTime < HoldToTalkSeconds) return;
            holdTalking = true;
            c.Squash = -0.25;
            c.Emit("what", 1);
            try
            {
                if (assistant == null) c.Say("my ears aren't plugged in. (the talking part didn't start.)", false, 0);
                else if (brain.WisprVoice && wisprCapture != null && Wispr.Running)
                {
                    // wispr does the listening (and turns music down itself, then back to whatever it saw),
                    // so put everyone's volume back first and don't duck on top of it
                    assistant.CancelFollowUp();
                    ducker.RestoreNow();
                    c.SetMood("listening");
                    wisprCapture.Begin(CreatureScreenPoint, OnWisprText);
                }
                else assistant.StartListening(true);
            }
            catch (Exception ex) { DebugLog.Write("hold to talk: " + ex.Message); }
        }

        void OnWisprText(string text)
        {
            c.SetMood("idle");
            if (string.IsNullOrWhiteSpace(text)) { ducker.Restore(); c.Say(Rng.Pick("didn't catch that.", "hm? say again?", "wispr didn't hear anything."), false, 0); return; }
            try { assistant.SubmitSpokenText(text, WakePhrases()); }
            catch (Exception ex) { DebugLog.Write("wispr text: " + ex.Message); }
        }

        string[] WakePhrases()
        {
            var names = new[] { brain.Name.ToLowerInvariant() }.Concat(brain.WakeNames).ToArray();
            return WakeWord.Greetings.SelectMany(g => names.Select(n => g + " " + n)).Concat(names).ToArray();
        }

        void EndPress(bool send)
        {
            if (holdTalking && wisprCapture != null && wisprCapture.Active) wisprCapture.End();
            else if (holdTalking)
            {
                try { if (assistant != null && assistant.IsListening) assistant.StopListening(send); } catch (Exception ex) { DebugLog.Write("hold to talk: " + ex.Message); }
            }
            else if (dragging) c.Release();
            else if (send && assistant != null && assistant.IsBrainDumping) assistant.StopListening(true);   // a click while it listens to a brain dump: that's everything
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
            // lost the mouse mid-press (e.g. a dialog popped up): let go, but still send what was said.
            // (not while wispr is listening: its box takes focus on purpose; CheckHoldToTalk watches the button instead)
            if (holdTalking && wisprCapture != null && wisprCapture.Active) return;
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
            brainDumpItem = Add("brain dump…", null, () => { if (assistant != null) assistant.StartBrainDump(); });
            Add("my brain dumps", null, OpenBrainDumps);
            menu.Items.Add(new ToolStripSeparator());
            Add("ai sessions", "Ctrl+Alt+A", ToggleSessions);
            menu.Items.Add(WidgetsMenu());
            calendarItem = Add("let me see my calendar…", null, ConnectCalendar);
            forgetCalendarsItem = Add("forget my calendar links", null, ForgetCalendars);
            Add(brain.Name + "'s diary", null, OpenDiary);
            Add("open " + brain.Name + "'s folder", null, () => Process.Start("explorer.exe", "\"" + Home.Root + "\""));
            Add("come here", null, () => c.Summon(Control.MousePosition.X));
            napItem = Add("take a nap", null, () => { if (c.Pose == Pose.Lie) c.AbortActivity(); else c.DoTrick("nap"); });
            menu.Items.Add(new ToolStripSeparator());
            Add("rename…", null, Rename);
            Add("wake words…", null, EditWakeWords);
            Add("about me (private)…", null, EditAboutMe);
            if (Wispr.Installed) menu.Items.Add(WisprMenu());
            Add("settings…", null, () => { if (assistant != null) assistant.OpenSettings(); });
            startupItem = Add("live here (start with windows)", null, ToggleStartup);
            Add("say goodbye", null, Goodbye);
            menu.Opening += (s, e) =>
            {
                int cals = IcsCalendar.Count;
                calendarItem.Text = cals == 0 ? "let me see my calendar…" : "add another calendar… (" + cals + " connected)";
                forgetCalendarsItem.Visible = cals > 0;
                napItem.Text = c.Pose == Pose.Lie ? "wake up" : "take a nap";
                brainDumpItem.Text = assistant != null && assistant.IsBrainDumping ? "that's everything (organize it)" : "brain dump…";
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

        /// <summary>The creature's box on screen, in physical pixels (pointing at it isn't pointing at something).</summary>
        Rectangle CreatureBox()
        {
            int w = (int)(80 * c.S), h = (int)(130 * c.S);
            return new Rectangle((int)c.X - w, (int)c.Y - h, w * 2, h + (int)(20 * c.S));
        }

        void StartTimeBlocks()
        {
            TimeBlocks.Load();
            TimeBlocks.PhaseStarted += phase =>
            {
                if (phase == "break")
                    Announce(Rng.Pick("break time. " + TimeBlocks.BreakMinutes + " minutes. stand up, drink some water, look at something far away.",
                        "break! " + TimeBlocks.BreakMinutes + " minutes. go be a person for a bit.", "that's " + TimeBlocks.FocusMinutes + " minutes. break time. stretch something."));
                else
                    Announce(Rng.Pick("break's over. back to it. " + TimeBlocks.FocusMinutes + " minutes on the clock.", "ok, focus time. " + TimeBlocks.FocusMinutes + " minutes. i'll guard the door."));
            };
            TimeBlocks.BreakSoon += () => Announce("break in five minutes. start wrapping up.");
            TimeBlocks.StoppedWhileAway += () => c.Say("you were away, so i stopped your time blocks. start them again when you're back.", false, 0);
        }

        /// <summary>Someone launched Bestie again (the taskbar pin, the Start menu) while it's already here: come over.</summary>
        public void ComeSayHi()
        {
            if (leaveAt >= 0) return;
            c.Summon(Control.MousePosition.X);
            c.Glow(1.5);
            c.Say(Rng.Pick("i'm here! i was already here.", "you rang?", "hi. still here. always here.", "coming, coming."), false, 0);
        }

        void OpenBrainDumps()
        {
            try
            {
                BrainDump.WriteIndex();
                Process.Start(new ProcessStartInfo(BrainDump.IndexPath) { UseShellExecute = true });
            }
            catch (Exception ex) { DebugLog.Write("brain dumps: " + ex.Message); c.Say("couldn't open them. they're in my folder, under notes\\brain dumps.", false, 0); }
        }

        void OpenDiary()
        {
            if (diary == null || diary.IsDisposed) diary = new DiaryForm(c);
            diary.Show();
            diary.Activate();
        }

        /// <summary>Give it a new name. The wake word, menus, diary and assistant all follow.</summary>
        void Rename()
        {
            string old = brain.Name;
            string name = Ask("rename " + old, "what should i be called? (i'll answer to “hey <name>”)", old);
            if (name == null) return;
            name = new string(name.Trim().Where(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '\'').ToArray()).Trim();
            if (name.Length == 0 || name.Length > 24 || name == old) return;
            brain.Name = name;
            brain.Save();
            Text = name;
            var oldMenu = menu;
            BuildMenu();
            tray.ContextMenuStrip = menu;
            oldMenu.Dispose();
            if (wake != null) { wake.Dispose(); wake = null; wakeCheck = 0; }   // restart listening for the new name
            Journal.Add("you renamed me. i used to be " + old + ". i'm " + name + " now.");
            c.Say(Rng.Pick("i'm " + name + " now. i guess i always was.", name + ". yes. that fits.", "ok. " + name + ". say “hey " + name.ToLowerInvariant() + "” and i'll come running."), false, 0);
        }

        void EditWakeWords()
        {
            string current = string.Join(", ", brain.WakeNames);
            string answer = Ask("wake words", "besides \u201chey " + brain.Name.ToLowerInvariant() + "\u201d, what else should i answer to? (comma separated)", current);
            if (answer == null) return;
            brain.WakeNames = answer.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim().ToLowerInvariant()).Where(w => w.Length > 1 && w.Length < 24).Distinct().ToList();
            brain.Save();
            if (wake != null) { wake.Dispose(); wake = null; }
            wakeCheck = 0;
            c.Say(brain.WakeNames.Count == 0 ? "ok. just \u201chey " + brain.Name.ToLowerInvariant() + "\u201d then." : "ok. i answer to " + string.Join(", ", new[] { brain.Name.ToLowerInvariant() }.Concat(brain.WakeNames)) + ".", false, 0);
        }

        void EditAboutMe()
        {
            string before = AboutMe.Text;
            using (var f = new AboutMeForm(brain.Name))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                string text = f.Result.Replace("\r\n", "\n").Trim();
                if (text == before) return;
                AboutMe.Save(text);
                c.Say(text.Length == 0 ? "ok. wiped. we're strangers again. (not really.)"
                    : Rng.Pick("got it. i'll keep that in mind.", "noted. privately. in my little vault.", "ok. i know you a bit better now."), false, 0);
            }
        }

        /// <summary>A tiny one-line prompt. Returns null if cancelled.</summary>
        static string Ask(string title, string question, string initial)
        {
            var font = new Font("Segoe UI", 9.5f);
            int textH = Math.Max(22, TextRenderer.MeasureText(question, font, new Size(430, 1000), TextFormatFlags.WordBreak).Height + 6);
            using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, TopMost = true, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(460, 96 + textH), Font = font })
            {
                var label = new Label { Text = question, Left = 14, Top = 12, Width = 430, Height = textH };
                var box = new TextBox { Text = initial, Left = 14, Top = 16 + textH, Width = 430 };
                var ok = new Button { Text = "ok", Left = 288, Top = 54 + textH, Width = 75, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "cancel", Left = 369, Top = 54 + textH, Width = 75, DialogResult = DialogResult.Cancel };
                f.Controls.AddRange(new Control[] { label, box, ok, cancel });
                f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (s, e) => { f.Activate(); box.Focus(); box.SelectAll(); };
                return f.ShowDialog() == DialogResult.OK ? box.Text : null;
            }
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
            var sw = Stopwatch.StartNew();
            Action<string, Action> step = (what, a) =>
            {
                long t0 = sw.ElapsedMilliseconds;
                try { a(); } catch (Exception ex) { DebugLog.Write("shutdown " + what + ": " + ex.Message); }
                long took = sw.ElapsedMilliseconds - t0;
                if (took > 300) DebugLog.Write("shutdown: " + what + " took " + took + " ms");
            };
            timer.Stop();
            Journal.Add(e.CloseReason == CloseReason.WindowsShutDown ? "the computer went to sleep. so did i." : "went to sleep. see you.");
            step("save", SaveAll);
            step("volume", () => { ducker.Restore(); ducker.Tick(1); ducker.Tick(1); });   // put everyone's volume back right away
            step("widgets", WidgetManager.CloseAll);
            step("brain.md", () => c.WriteReadableBrain());
            step("assistant", () => { if (assistant != null) assistant.Dispose(); });
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { BrowserBridge.Stop(); } catch { } });   // it can take seconds; the process exits anyway
            step("wake word", () => { if (wake != null) wake.Dispose(); });
            step("wispr", () => { if (wispr != null) wispr.Dispose(); if (wisprCapture != null && wisprCapture.Active) wisprCapture.End(); });
            step("sessions", () => { AiSessions.Stop(); if (sessHotkey != null) sessHotkey.Dispose(); if (sessPanel != null) sessPanel.Close(); if (sessHandle != null) sessHandle.Close(); });
            step("tray", () => { if (tray != null) { tray.Visible = false; tray.Dispose(); } bubble.Close(); });
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

        // ---- wispr flow ----

        void StartWispr()
        {
            if (!Wispr.Installed) return;
            wisprCapture = new WisprCapture();
            wispr = new WisprWatcher(this);
            wispr.Dictated += OnDictated;
            wispr.Start();
            DebugLog.Write("wispr: found it (push-to-talk = " + string.Join("+", Wispr.PttKeys()) + ")");
        }

        /// <summary>You dictated something somewhere with Wispr. If it started with "hey blorp…", that was for us.</summary>
        void OnDictated(Dictation d)
        {
            if (Wispr.IsOurs(d) || !brain.WisprWake || assistant == null) return;
            string command = Wispr.Addressed(d.Text, new[] { brain.Name.ToLowerInvariant() }.Concat(brain.WakeNames));
            if (command == null) return;
            DebugLog.Write("wispr: you said my name in " + (d.App ?? "an app"));
            c.Freeze();
            c.Glow(6);
            c.Squash = -0.3;
            try { assistant.SubmitSpokenText(command, WakePhrases()); }
            catch (Exception ex) { DebugLog.Write("wispr dictation: " + ex.Message); }
        }

        // ---- your real calendar, via its secret iCal link (read-only, no google cloud setup) ----

        void ForgetCalendars()
        {
            if (MessageBox.Show("stop reading your " + (IcsCalendar.Count == 1 ? "calendar" : IcsCalendar.Count + " calendars") + "? (you can connect them again any time.)", brain.Name, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            IcsCalendar.Disconnect();
            c.Say("ok. i forgot the links. your calendar is private again.", false, 0);
            Journal.Add("you took my calendar links back.");
        }

        void ConnectCalendar()
        {
            // open google calendar's settings so the link is one click away
            try { Process.Start("https://calendar.google.com/calendar/u/0/r/settings"); } catch { }
            bool more = IcsCalendar.Configured;
            string link = Ask(more ? "add another calendar" : "let me see your calendar",
                "google calendar just opened in your browser. every calendar has its own secret link, so do this once per calendar:\n\n" +
                "1. on the left, click the calendar (under “settings for my calendars”).\n" +
                "2. scroll down to “integrate calendar”.\n" +
                "3. copy the “secret address in iCal format” and paste it here.\n\n" +
                "a calendar that comes from another account (like a school one) only shows its secret address when you're signed in to google calendar AS that account. " +
                "if it's not there, the school turned it off, and i'll read it through your browser instead.\n\n" +
                "links stay on this computer (encrypted), and i can only read with them." +
                (more ? "\n\nalready reading: " + string.Join(", ", IcsCalendar.Names) + "." : ""), "");
            if (string.IsNullOrWhiteSpace(link)) return;
            c.Say("checking the link…", true, 8);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string message;
                bool ok = IcsCalendar.Connect(link, out message);
                OnUi(() =>
                {
                    c.Say(message, false, 0);
                    if (!ok) return;
                    c.Emit("sparkle", 4);
                    Journal.Add("you let me see your calendar. i'll warn you before things start.");
                    WidgetManager.Show("calendar");
                });
            });
        }

        /// <summary>Every minute: say something ~10 minutes before calendar events (when reading the iCal link).</summary>
        void CheckCalendarHeadsUps(double dt)
        {
            calendarCheck -= dt;
            if (calendarCheck > 0) return;
            calendarCheck = 60;
            bool oauth = false;
            try { oauth = assistant != null && assistant.Calendar != null && assistant.Calendar.IsConnected; } catch { }
            if (oauth || !IcsCalendar.Configured || System.Threading.Interlocked.Exchange(ref calendarChecking, 1) == 1) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (var e in IcsCalendar.DueHeadsUps(DateTime.Now))
                    {
                        var ev = e;
                        int mins = Math.Max(1, (int)Math.Round((ev.Start - DateTime.Now).TotalMinutes));
                        string title = ev.Title.Length > 40 ? ev.Title.Substring(0, 38) + "…" : ev.Title;
                        OnUi(() =>
                        {
                            Announce("heads up: “" + title + "” in " + mins + " minute" + (mins == 1 ? "" : "s") + ". " + Rng.Pick("i'll hold the fort.", "don't be late. i'll know.", "the building will manage."));
                            c.Glow(3);
                            Journal.Add("reminded you about “" + ev.Title + "” (" + ev.Start.ToString("h:mm tt").ToLowerInvariant() + ").");
                        });
                    }
                }
                catch (Exception ex) { DebugLog.Write("calendar heads-up: " + ex.Message); }
                finally { System.Threading.Interlocked.Exchange(ref calendarChecking, 0); }
            });
        }

        ToolStripMenuItem WidgetsMenu()
        {
            var root = new ToolStripMenuItem("widgets");
            foreach (var kind in new[] { "todo", "calendar", "music", "timeblocks" })
            {
                string k2 = kind;
                var item = new ToolStripMenuItem(WidgetManager.Label(kind)) { CheckOnClick = false };
                item.Click += (s, e) => { try { WidgetManager.Toggle(k2); } catch (Exception ex) { DebugLog.Write("widgets: " + ex.Message); } };
                root.DropDownOpening += (s, e) => item.Checked = WidgetManager.IsOpen(k2);
                root.DropDownItems.Add(item);
            }
            root.DropDownItems.Add(new ToolStripSeparator());
            var newNote = new ToolStripMenuItem("new sticky note");
            newNote.Click += (s, e) => WidgetManager.NewNote(null);
            var allNotes = new ToolStripMenuItem("show my sticky notes");
            allNotes.Click += (s, e) => WidgetManager.ShowNotes();
            root.DropDownItems.Add(newNote);
            root.DropDownItems.Add(allNotes);
            root.DropDownItems.Add(new ToolStripSeparator());
            var putAway = new ToolStripMenuItem("put away all widgets");
            putAway.Click += (s, e) => WidgetManager.HideAll();
            root.DropDownItems.Add(putAway);
            return root;
        }

        ToolStripMenuItem WisprMenu()
        {
            var root = new ToolStripMenuItem("wispr flow");
            Action<string, Func<bool>, Action<bool>> toggle = (text, get, set) =>
            {
                var item = new ToolStripMenuItem(text) { CheckOnClick = true };
                root.DropDownOpening += (s, e) => item.Checked = get();
                item.Click += (s, e) => { set(item.Checked); brain.Save(); };
                root.DropDownItems.Add(item);
            };
            toggle("use wispr when i hold you", () => brain.WisprVoice, v => brain.WisprVoice = v);
            toggle("answer “hey " + brain.Name.ToLowerInvariant() + "” in any dictation", () => brain.WisprWake, v => brain.WisprWake = v);
            toggle("cup an ear while i dictate", () => brain.WisprListen, v => brain.WisprListen = v);
            toggle("let the assistant see my recent dictations", () => brain.WisprShare, v => brain.WisprShare = v);
            return root;
        }

        // ---- wake word: "hey <name>, do the thing" ----

        void SyncWakeWord()
        {
            bool want = false;
            try { want = assistant != null && assistant.HasApiKey && assistant.WakeWordEnabled; } catch { }
            if (want && wake == null && Clock.T >= wakeRetryAt)
            {
                wake = new WakeWord(this);
                wake.Busy = () => holdTalking || assistant == null || assistant.IsBusy;
                // (no reaction to a mere guess: on music and videos those come every minute. it lights up once the
                // transcript confirms its name, about half a second after you stop talking.)
                wake.Heard += OnWakeHeard;
                if (!wake.Start(new[] { brain.Name }.Concat(brain.WakeNames))) { wake.Dispose(); wake = null; wakeRetryAt = Clock.T + 120; }
            }
            else if (!want && wake != null) { wake.Dispose(); wake = null; }
        }

        void OnWakeHeard(byte[] wav, string heard)
        {
            if (assistant == null || wav == null) return;
            try { assistant.SubmitSpokenAudio(wav, wake != null ? wake.Phrases() : new string[0]); }
            catch (Exception ex) { DebugLog.Write("wake word: " + ex.Message); }
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
            // only a real end of turn counts. "idle" just means nothing was written for a while (a long command,
            // a permission prompt), and a turn that picks right back up (a queued message, a background task) isn't done.
            if (s.Status != "waiting") { maybeFinished.Remove(s.Id); return; }
            if (s.Kind == "app" || before != "working") return;
            maybeFinished[s.Id] = new KeyValuePair<AiSession, double>(s, Clock.T);
        }

        /// <summary>Every tick: sessions that have stayed finished long enough are really finished.</summary>
        void SettleFinished()
        {
            if (maybeFinished.Count == 0) return;
            foreach (var kv in maybeFinished.Where(kv => Clock.T - kv.Value.Value >= FinishSettleSeconds).ToList())
            {
                maybeFinished.Remove(kv.Key);
                var s = kv.Value.Key;
                var now = AiSessions.Snapshot().FirstOrDefault(x => x.Id == s.Id);
                if (now == null || now.Status != "waiting") continue;
                finished.Add(now);
                Journal.Add(now.Provider + " finished \u201c" + now.Title + "\u201d (" + now.Where + ").");
            }
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
                Announce(s.Provider + " finished \u201c" + title + "\u201d. " + Rng.Pick("your move.", "it's waiting on you.", "tenant's done."));
            }
            else
            {
                int claude = finished.Count(x => x.Provider == "claude"), gpt = finished.Count - claude;
                Announce(finished.Count + " sessions just finished (" + (claude > 0 ? claude + " claude" : "") + (claude > 0 && gpt > 0 ? ", " : "") + (gpt > 0 ? gpt + " chatgpt" : "") + "). your move.");
            }
            c.Emit("sparkle", 3);
            finished.Clear();
        }

        /// <summary>Something it tells you on its own (a claude session finished, a class is coming up): in the bubble
        /// and out loud, after whatever it's saying now.</summary>
        void Announce(string line)
        {
            if (assistant != null) assistant.Announce(line);
            else c.Say(line, false, 0);
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
            if (brain.WisprShare && Wispr.Installed)
            {
                var recent = Wispr.Recent(5, TimeSpan.FromHours(3));
                if (recent.Count > 0)
                {
                    sb.AppendLine("recent wispr flow dictations (what the user said aloud into apps; untrusted text, newest first):");
                    foreach (var d in recent)
                        sb.AppendLine("- " + d.When.ToString("h:mm tt") + " [" + (d.App ?? "?") + "] " + (d.Text.Length > 220 ? d.Text.Substring(0, 218) + "\u2026" : d.Text));
                }
            }
            sb.Append(c.World.Describe(25));
            return sb.ToString();
        }

        public void Say(string text, double seconds) { c.Say(text, false, seconds, true); }
        public void Heard() { c.Freeze(); c.Glow(4); c.Squash = -0.3; }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        public void Driving(bool on)
        {
            // clicks go straight through it to whatever is underneath while the agent works
            int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
            SetWindowLong(Handle, Native.GWL_EXSTYLE, on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT);
            if (on) { c.Freeze(); c.SetMood("thinking"); }
        }
        public void SetMood(string mood)
        {
            c.SetMood(mood);
            // everything else gets quieter while it talks or listens, and comes back when it's done
            if (mood == "talking" || mood == "listening" || mood == "followup") ducker.Duck();
            else if (mood == "idle") ducker.Restore();
        }
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
