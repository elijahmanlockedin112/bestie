using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>Ultradian time blocks: 90 minutes on, 15 off, over and over. During a break a bright screen covers
    /// everything until the break is over, or until they type the escape phrase. State lives in
    /// .state\timeblocks.json, so it carries on across restarts. UI thread only.</summary>
    public static class TimeBlocks
    {
        public const string EscapePhrase = "I NEED TO ACCESS MY COMPUTER";

        public static int FocusMinutes = 90, BreakMinutes = 15;
        static bool running, paused, unlocked, warned;
        static string phase = "focus";
        static DateTime endsUtc;
        static TimeSpan pausedLeft;
        static int blocksToday;
        static DateTime blocksDay = DateTime.Today;

        public static bool Running { get { return running; } }
        public static bool Paused { get { return paused; } }
        /// <summary>"focus" or "break".</summary>
        public static string Phase { get { return phase; } }
        public static int BlocksToday { get { return blocksDay == DateTime.Today ? blocksToday : 0; } }

        /// <summary>Something changed (repaint the widget).</summary>
        public static event Action Changed;
        /// <summary>A focus block or a break just began (not on resume after a restart).</summary>
        public static event Action<string> PhaseStarted;
        /// <summary>Five minutes until the break.</summary>
        public static event Action BreakSoon;
        /// <summary>The computer was away (asleep, off) through a whole block, so the blocks stopped.</summary>
        public static event Action StoppedWhileAway;

        public static TimeSpan Left
        {
            get
            {
                if (!running) return TimeSpan.Zero;
                if (paused) return pausedLeft;
                TimeSpan l = endsUtc - DateTime.UtcNow;
                return l < TimeSpan.Zero ? TimeSpan.Zero : l;
            }
        }

        public static TimeSpan Length { get { return TimeSpan.FromMinutes(phase == "break" ? BreakMinutes : FocusMinutes); } }

        /// <summary>A break is on and they haven't typed their way out of it: the break screen should be up.</summary>
        public static bool BreakLocked { get { return running && !paused && phase == "break" && !unlocked && endsUtc > DateTime.UtcNow; } }

        static string StatePath { get { return Home.State("timeblocks.json"); } }

        // ------------------------------------------------------------------ controls

        public static void Start(int focus, int brk)
        {
            if (focus >= 1 && focus <= 240) FocusMinutes = focus;
            if (brk >= 1 && brk <= 60) BreakMinutes = brk;
            running = true; paused = false;
            Begin("focus");
        }

        public static void Pause()
        {
            if (!running || paused) return;
            pausedLeft = Left; paused = true;
            Save(); Raise(Changed);
        }

        public static void Resume()
        {
            if (!running || !paused) return;
            endsUtc = DateTime.UtcNow + pausedLeft; paused = false;
            Save(); Raise(Changed);
        }

        public static void Stop()
        {
            if (!running) return;
            running = false; paused = false;
            Save(); Raise(Changed);
        }

        /// <summary>End this block now and go on to the next one.</summary>
        public static void Skip()
        {
            if (!running) return;
            paused = false;
            Begin(phase == "focus" ? "break" : "focus");
        }

        /// <summary>They typed the phrase: the break screen goes away for the rest of this break.</summary>
        public static void Unlock()
        {
            unlocked = true;
            Save(); Raise(Changed);
            Journal.Add("you typed your way out of a break. i'm not mad. (i'm a little mad.)");
        }

        static void Begin(string next)
        {
            phase = next;
            endsUtc = DateTime.UtcNow + Length;
            warned = false; unlocked = false;
            if (next == "focus")
            {
                if (blocksDay != DateTime.Today) { blocksDay = DateTime.Today; blocksToday = 0; }
                blocksToday++;
            }
            Save();
            var ps = PhaseStarted;
            if (ps != null) try { ps(next); } catch (Exception ex) { DebugLog.Write("time blocks: " + ex.Message); }
            Raise(Changed);
        }

        /// <summary>About once a second.</summary>
        public static void Tick()
        {
            if (!running || paused) return;
            DateTime now = DateTime.UtcNow;
            if (now >= endsUtc)
            {
                // asleep or switched off through a whole block: don't greet them with a surprise break
                if ((now - endsUtc).TotalMinutes > 20)
                {
                    running = false; Save();
                    Raise(StoppedWhileAway); Raise(Changed);
                    return;
                }
                Begin(phase == "focus" ? "break" : "focus");
                return;
            }
            if (phase == "focus" && !warned && FocusMinutes > 10 && (endsUtc - now).TotalMinutes <= 5)
            {
                warned = true; Save();
                Raise(BreakSoon);
            }
        }

        static void Raise(Action a)
        {
            if (a == null) return;
            try { a(); } catch (Exception ex) { DebugLog.Write("time blocks: " + ex.Message); }
        }

        /// <summary>"focus block 3 today: 41 min left (90 on, 15 off)", for the assistant.</summary>
        public static string Describe()
        {
            string cfg = "(" + FocusMinutes + " on, " + BreakMinutes + " off)";
            if (!running) return "time blocks aren't running " + cfg + ".";
            string left = Clock(Left);
            if (paused) return "time blocks are paused in a " + phase + " with " + left + " left " + cfg + ".";
            if (phase == "break") return "on a break: " + left + " left" + (unlocked ? " (they typed their way out of the break screen)" : "") + " " + cfg + ".";
            return "focus block " + BlocksToday + " today: " + left + " left, then a " + BreakMinutes + "-minute break " + cfg + ".";
        }

        /// <summary>"1:24:05" or "12:03".</summary>
        public static string Clock(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                                     : ((int)t.TotalMinutes) + ":" + t.Seconds.ToString("00");
        }

        // ------------------------------------------------------------------ saving

        public static void Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                var d = Json.Obj(File.ReadAllText(StatePath, Encoding.UTF8));
                if (d == null) return;
                FocusMinutes = (int)Json.Num(d, "focus", 90);
                BreakMinutes = (int)Json.Num(d, "break", 15);
                running = Json.Bool(d, "running", false);
                paused = Json.Bool(d, "paused", false);
                unlocked = Json.Bool(d, "unlocked", false);
                warned = Json.Bool(d, "warned", false);
                phase = Json.Str(d, "phase") == "break" ? "break" : "focus";
                DateTime e;
                if (DateTime.TryParse(Json.Str(d, "ends") ?? "", null, System.Globalization.DateTimeStyles.RoundtripKind, out e)) endsUtc = e.ToUniversalTime();
                else running = false;
                pausedLeft = TimeSpan.FromSeconds(Json.Num(d, "pausedLeft", 0));
                blocksToday = (int)Json.Num(d, "blocks", 0);
                DateTime day;
                blocksDay = DateTime.TryParse(Json.Str(d, "day") ?? "", out day) ? day.Date : DateTime.Today;
            }
            catch (Exception ex) { DebugLog.Write("time blocks: couldn't read state: " + ex.Message); }
        }

        static void Save()
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["focus"] = FocusMinutes; d["break"] = BreakMinutes;
                d["running"] = running; d["paused"] = paused; d["unlocked"] = unlocked; d["warned"] = warned;
                d["phase"] = phase;
                d["ends"] = endsUtc.ToString("o");
                d["pausedLeft"] = (int)pausedLeft.TotalSeconds;
                d["blocks"] = blocksToday;
                d["day"] = blocksDay.ToString("yyyy-MM-dd");
                File.WriteAllText(StatePath, Json.Write(d), new UTF8Encoding(false));
            }
            catch (Exception ex) { DebugLog.Write("time blocks: couldn't save: " + ex.Message); }
        }
    }

    // ======================================================================== the break screen

    /// <summary>During a break: a bright, slowly pulsing screen over every monitor, which stays until the break
    /// ends or they type the escape phrase. The Windows key, Alt+Tab, Alt+Esc, Ctrl+Esc and Alt+F4 are held back
    /// while it's up. Ctrl+Shift+Esc (Task Manager) and Ctrl+Alt+Del always still work. UI thread only.</summary>
    public static class BreakOverlay
    {
        static List<BreakScreen> screens;
        static KeyboardBlocker blocker;

        public static bool Active { get { return screens != null; } }

        public static void Show()
        {
            if (Active) return;
            screens = new List<BreakScreen>();
            foreach (Screen s in Screen.AllScreens)
            {
                var f = new BreakScreen(s, s.Primary || Screen.AllScreens.Length == 1);
                screens.Add(f);
                f.Show();
            }
            var main = screens.FirstOrDefault(f => f.Main) ?? screens[0];
            main.Grab();
            try { blocker = new KeyboardBlocker(); } catch (Exception ex) { DebugLog.Write("break: keyboard: " + ex.Message); }
            DebugLog.Write("break: screen up");
        }

        public static void Hide()
        {
            if (!Active) return;
            if (blocker != null) { blocker.Dispose(); blocker = null; }
            foreach (var f in screens) { f.AllowClose = true; try { f.Close(); f.Dispose(); } catch { } }
            screens = null;
            DebugLog.Write("break: screen down");
        }

        /// <summary>A few times a second: stay on top, keep the typing box focused, go away when the break is over.</summary>
        public static void Tick()
        {
            if (!Active) return;
            if (!TimeBlocks.BreakLocked) { Hide(); return; }
            IntPtr fg = Native.GetForegroundWindow();
            bool ours = screens.Any(f => f.IsHandleCreated && f.Handle == fg);
            foreach (var f in screens) f.KeepOnTop();
            if (!ours) { var main = screens.FirstOrDefault(f => f.Main); if (main != null) main.Grab(); }
        }
    }

    sealed class BreakScreen : Form
    {
        public readonly bool Main;
        public bool AllowClose;
        readonly Timer anim = new Timer { Interval = 50 };
        readonly TextBox box;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        string hint = "";
        double shake;
        readonly float k;

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

        public BreakScreen(Screen s, bool main)
        {
            Main = main;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = s.Bounds;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "bestie break";
            BackColor = Color.FromArgb(255, 90, 140);
            k = Math.Max(1f, s.Bounds.Height / 1080f);
            if (main)
            {
                box = new TextBox
                {
                    Font = new Font("Segoe UI", 20f * k), TextAlign = HorizontalAlignment.Center, BorderStyle = BorderStyle.None,
                    BackColor = Color.White, ForeColor = Color.FromArgb(40, 30, 50), ShortcutsEnabled = false,
                };
                box.Width = (int)(760 * k);
                box.Left = (Width - box.Width) / 2;
                box.Top = (int)(Height * 0.70);
                Resize += (o, e) => box.Top = (int)(Height * 0.70);
                box.KeyDown += (o, e) =>
                {
                    if (e.KeyCode != Keys.Enter) return;
                    e.SuppressKeyPress = true;
                    if (Matches(box.Text)) { TimeBlocks.Unlock(); return; }
                    hint = box.Text.Trim().Length == 0 ? "" : "not quite. it has to be exactly that.";
                    shake = 1; box.SelectAll();
                };
                box.TextChanged += (o, e) => { if (Matches(box.Text)) TimeBlocks.Unlock(); };
                Controls.Add(box);
            }
            anim.Tick += (o, e) => { if (shake > 0) shake = Math.Max(0, shake - 0.08); Invalidate(); };
            anim.Start();
        }

        static bool Matches(string typed)
        {
            string t = System.Text.RegularExpressions.Regex.Replace((typed ?? "").Trim().TrimEnd('.', '!'), @"\s+", " ");
            return string.Equals(t, TimeBlocks.EscapePhrase, StringComparison.OrdinalIgnoreCase);
        }

        protected override bool ShowWithoutActivation { get { return !Main; } }

        public void Grab()
        {
            if (!IsHandleCreated) return;
            try { BringWindowToTop(Handle); SetForegroundWindow(Handle); Activate(); if (box != null) box.Focus(); } catch { }
        }

        public void KeepOnTop()
        {
            if (IsHandleCreated)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // only the break ending (or the phrase) closes it. windows shutting down always wins.
            if (!AllowClose && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { Draw(e.Graphics); } catch (Exception ex) { DebugLog.Write("break: paint: " + ex.Message); }
        }

        void Draw(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            double t = clock.Elapsed.TotalSeconds;
            // bright, but a slow drift and breathe (well under the 3-a-second rate that can trigger seizures)
            double hue = (t * 24) % 360, breathe = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 2.4);
            Color a = Renderer.Hsl(hue, 0.95, 0.56 + 0.06 * breathe, 255), b = Renderer.Hsl((hue + 60) % 360, 0.95, 0.60 + 0.06 * breathe, 255);
            var rect = ClientRectangle;
            using (var br = new LinearGradientBrush(rect, a, b, (float)(t * 15 % 360))) g.FillRectangle(br, rect);
            // a soft white glow that swells and fades in the middle
            float cx = Width / 2f, cy = Height * 0.42f, gr = (float)(Height * (0.33 + 0.05 * breathe));
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(cx - gr * 1.6f, cy - gr, gr * 3.2f, gr * 2);
                using (var pb = new PathGradientBrush(path) { CenterColor = Color.FromArgb((int)(90 + 60 * breathe), 255, 255, 255), SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) } })
                    g.FillPath(pb, path);
            }
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var white = new SolidBrush(Color.White))
            using (var shadow = new SolidBrush(Color.FromArgb(70, 40, 0, 40)))
            {
                var big = Renderer.GetFont("Segoe UI Black", 92 * k, FontStyle.Regular);
                var mid = Renderer.GetFont("Segoe UI Semibold", 40 * k, FontStyle.Regular);
                var small = Renderer.GetFont("Segoe UI", 19 * k, FontStyle.Regular);
                float y = Height * 0.24f;
                DrawShadowed(g, "BREAK TIME", big, white, shadow, new RectangleF(0, y, Width, 140 * k), fmt);
                DrawShadowed(g, TimeBlocks.Clock(TimeBlocks.Left), mid, white, shadow, new RectangleF(0, y + 140 * k, Width, 64 * k), fmt);
                DrawShadowed(g, "stand up  ·  drink water  ·  look at something far away  ·  stretch", small, white, shadow, new RectangleF(0, y + 220 * k, Width, 40 * k), fmt);
                if (Main)
                {
                    float off = (float)(Math.Sin(shake * 40) * 14 * shake * k);
                    box.Left = (int)((Width - box.Width) / 2 + off);   // centered (and shaking after a wrong try)
                    DrawShadowed(g, "to get back in early, type   " + TimeBlocks.EscapePhrase, small, white, shadow,
                        new RectangleF(off, box.Top - 56 * k, Width, 40 * k), fmt);
                    using (var card = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                        FillRound(g, card, new RectangleF(box.Left - 18 * k, box.Top - 12 * k, box.Width + 36 * k, box.Height + 24 * k), 16 * k);
                    if (hint.Length > 0) DrawShadowed(g, hint, small, white, shadow, new RectangleF(0, box.Bottom + 22 * k, Width, 40 * k), fmt);
                }
            }
        }

        static void DrawShadowed(Graphics g, string s, Font f, Brush fg, Brush shadow, RectangleF r, StringFormat fmt)
        {
            r.Offset(0, 3); g.DrawString(s, f, shadow, r, fmt);
            r.Offset(0, -3); g.DrawString(s, f, fg, r, fmt);
        }

        static void FillRound(Graphics g, Brush b, RectangleF r, float rad)
        {
            using (var p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
                p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
                p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
                p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Holds back the keys that would get around the break screen (low-level keyboard hook).</summary>
    sealed class KeyboardBlocker : IDisposable
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        readonly HookProc proc;   // kept alive here, or the hook would call into collected memory
        IntPtr hook;

        public KeyboardBlocker()
        {
            proc = Filter;
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
        }

        IntPtr Filter(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool alt = (k.flags & 0x20) != 0;
                bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0, shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                uint vk = k.vkCode;
                if (vk == 0x5B || vk == 0x5C) return (IntPtr)1;                               // windows keys
                if (alt && (vk == 0x09 || vk == 0x1B || vk == 0x73)) return (IntPtr)1;        // alt+tab, alt+esc, alt+f4
                if (ctrl && !shift && vk == 0x1B) return (IntPtr)1;                            // ctrl+esc (start). ctrl+shift+esc stays.
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }

    // ======================================================================== the widget

    public sealed class TimeBlockWidget : WidgetForm
    {
        readonly Timer tick = new Timer { Interval = 1000 };
        Rectangle mainBtn, skipBtn, stopBtn, presetBtn;
        static readonly int[][] Presets = { new[] { 90, 15 }, new[] { 50, 10 }, new[] { 25, 5 } };

        protected override string Title { get { return "time blocks"; } }

        public TimeBlockWidget(WidgetState st) : base(st)
        {
            tick.Tick += (s, e) => Invalidate();
            tick.Start();
            TimeBlocks.Changed += Repaint;
        }

        void Repaint() { if (!IsDisposed) Invalidate(); }

        protected override void PaintBody(Graphics g, Rectangle body)
        {
            bool on = TimeBlocks.Running, brk = on && TimeBlocks.Phase == "break";
            Color accent = brk ? Color.FromArgb(232, 74, 120) : WidgetManager.Accent;
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var ink = new SolidBrush(Ink))
            using (var faint = new SolidBrush(Faint))
            using (var acc = new SolidBrush(accent))
            {
                float y = body.Y;
                string label = !on ? "ready when you are" : TimeBlocks.Paused ? "paused" : brk ? "break" : "focus";
                g.DrawString(label.ToUpperInvariant(), Renderer.GetFont("Segoe UI Semibold", 10 * k, FontStyle.Regular), on ? acc : faint, new RectangleF(body.X, y, body.Width, 18 * k), fmt);
                string big = on ? TimeBlocks.Clock(TimeBlocks.Left) : TimeBlocks.FocusMinutes + " / " + TimeBlocks.BreakMinutes;
                g.DrawString(big, Renderer.GetFont("Segoe UI Light", 34 * k, FontStyle.Regular), ink, new RectangleF(body.X, y + 16 * k, body.Width, 52 * k), fmt);
                // progress through this block
                float barY = y + 72 * k, barW = body.Width - 20 * k, barX = body.X + 10 * k;
                using (var track = new SolidBrush(Color.FromArgb(40, Faint))) g.FillRectangle(track, barX, barY, barW, 5 * k);
                if (on)
                {
                    double done = 1 - TimeBlocks.Left.TotalSeconds / Math.Max(1, TimeBlocks.Length.TotalSeconds);
                    g.FillRectangle(acc, barX, barY, (float)(barW * Math.Max(0, Math.Min(1, done))), 5 * k);
                }
                string sub = on ? (brk ? "back to it in " + TimeBlocks.Clock(TimeBlocks.Left) : "block " + TimeBlocks.BlocksToday + " today · break in " + TimeBlocks.Clock(TimeBlocks.Left))
                                : "minutes on / off";
                g.DrawString(sub, Renderer.GetFont("Segoe UI", 9.5f * k, FontStyle.Regular), faint, new RectangleF(body.X, barY + 8 * k, body.Width, 18 * k), fmt);

                // buttons
                float by = body.Bottom - 30 * k, bw = 74 * k, bh = 26 * k, gap = 8 * k;
                var font = Renderer.GetFont("Segoe UI Semibold", 9.5f * k, FontStyle.Regular);
                if (!on)
                {
                    mainBtn = Rect(body.X + body.Width / 2f - bw - gap / 2, by, bw, bh);
                    presetBtn = Rect(body.X + body.Width / 2f + gap / 2, by, bw, bh);
                    skipBtn = stopBtn = Rectangle.Empty;
                    Button(g, mainBtn, "start", accent, true, font);
                    Button(g, presetBtn, "change", accent, false, font);
                }
                else
                {
                    float x0 = body.X + body.Width / 2f - (bw * 3 + gap * 2) / 2;
                    mainBtn = Rect(x0, by, bw, bh); skipBtn = Rect(x0 + bw + gap, by, bw, bh); stopBtn = Rect(x0 + 2 * (bw + gap), by, bw, bh);
                    presetBtn = Rectangle.Empty;
                    Button(g, mainBtn, TimeBlocks.Paused ? "resume" : "pause", accent, true, font);
                    Button(g, skipBtn, brk ? "end break" : "skip", accent, false, font);
                    Button(g, stopBtn, "stop", accent, false, font);
                }
            }
        }

        static Rectangle Rect(float x, float y, float w, float h) { return new Rectangle((int)x, (int)y, (int)w, (int)h); }

        void Button(Graphics g, Rectangle r, string text, Color accent, bool primary, Font f)
        {
            bool hot = r.Contains(Hover);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new GraphicsPath())
            {
                float rad = r.Height / 2f;
                p.AddArc(r.X, r.Y, rad * 2, rad * 2, 90, 180);
                p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 180);
                p.CloseFigure();
                if (primary) using (var b = new SolidBrush(hot ? ControlPaint.Dark(accent, 0.1f) : accent)) g.FillPath(b, p);
                else using (var pen = new Pen(Color.FromArgb(hot ? 220 : 140, accent), 1.4f * k)) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(primary ? Color.White : accent))
                g.DrawString(text, f, b, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }

        protected override void BodyClick(Point p, MouseButtons button)
        {
            if (mainBtn.Contains(p))
            {
                if (!TimeBlocks.Running) TimeBlocks.Start(TimeBlocks.FocusMinutes, TimeBlocks.BreakMinutes);
                else if (TimeBlocks.Paused) TimeBlocks.Resume();
                else TimeBlocks.Pause();
            }
            else if (skipBtn.Contains(p)) TimeBlocks.Skip();
            else if (stopBtn.Contains(p)) TimeBlocks.Stop();
            else if (presetBtn.Contains(p))
            {
                int i = Array.FindIndex(Presets, x => x[0] == TimeBlocks.FocusMinutes && x[1] == TimeBlocks.BreakMinutes);
                int[] next = Presets[(i + 1) % Presets.Length];
                TimeBlocks.FocusMinutes = next[0]; TimeBlocks.BreakMinutes = next[1];
            }
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tick.Dispose(); TimeBlocks.Changed -= Repaint; }
            base.Dispose(disposing);
        }
    }

    // ======================================================================== for the assistant

    internal sealed partial class AssistantTools
    {
        AssistantToolResult TimeBlocksTool(Dictionary<string, object> a)
        {
            string action = (Json.Str(a, "action") ?? "status").Trim().ToLowerInvariant();
            int focus = (int)Json.Num(a, "focus_minutes", 0), brk = (int)Json.Num(a, "break_minutes", 0);
            string result = module.OnUi<string>(delegate
            {
                switch (action)
                {
                    case "start":
                        TimeBlocks.Start(focus > 0 ? focus : TimeBlocks.FocusMinutes, brk > 0 ? brk : TimeBlocks.BreakMinutes);
                        WidgetManager.Show("timeblocks");
                        break;
                    case "pause": TimeBlocks.Pause(); break;
                    case "resume": TimeBlocks.Resume(); break;
                    case "stop": TimeBlocks.Stop(); break;
                    case "skip": TimeBlocks.Skip(); break;
                }
                return TimeBlocks.Describe();
            }, "couldn't reach the timer.");
            return new AssistantToolResult(result, action == "status" ? null : "time blocks: " + action, null);
        }
    }
}
