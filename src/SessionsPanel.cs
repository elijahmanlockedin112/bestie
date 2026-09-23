using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Bestie
{
    static class AiColors
    {
        public static readonly Color Claude = Color.FromArgb(217, 119, 87);
        public static readonly Color ChatGpt = Color.FromArgb(16, 163, 127);
        public static readonly Color Working = Color.FromArgb(46, 160, 67);
        public static readonly Color Waiting = Color.FromArgb(227, 160, 8);
        public static readonly Color Paper = Color.FromArgb(252, 248, 238);
        public static readonly Color Ink = Color.FromArgb(52, 46, 60);
        public static readonly Color Faint = Color.FromArgb(140, 130, 140);
        public static readonly Color Edge = Color.FromArgb(214, 204, 190);

        public static Color Of(string provider) { return provider == "chatgpt" ? ChatGpt : Claude; }

        /// <summary>Tiny brand-ish marks: an asterisk for Claude, a ring for ChatGPT.</summary>
        public static void Mark(Graphics g, string provider, float cx, float cy, float r)
        {
            using (var pen = new Pen(Of(provider), Math.Max(1.5f, r * 0.34f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (provider == "chatgpt")
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var st = g.Save();
                        g.TranslateTransform(cx, cy);
                        g.RotateTransform(i * 60);
                        g.DrawEllipse(pen, -r * 0.45f, -r, r * 0.9f, r * 2);
                        g.Restore(st);
                    }
                }
                else
                {
                    for (int i = 0; i < 6; i++)
                    {
                        double a = i * Math.PI / 6;
                        g.DrawLine(pen, cx - (float)Math.Cos(a) * r, cy - (float)Math.Sin(a) * r, cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                    }
                }
            }
        }
    }

    /// <summary>The little tab stuck to the right edge of the main screen. It only shows up while some AI session
    /// is working or waiting on you. Click it for the full list.</summary>
    public sealed class SessionsHandle : LayeredWindow
    {
        readonly Action onClick;
        Surface surf;
        float k = 1;
        int[] counts = new int[4];   // claude working, claude waiting, chatgpt working, chatgpt waiting
        bool wanted, hover, shown;
        double t, redraw;
        Rectangle area;

        public SessionsHandle(Action onClick) { this.onClick = onClick; Cursor = Cursors.Hand; }

        public void SetData(List<AiSession> list, Rectangle workArea, float scale, bool panelOpen)
        {
            counts = new[] {
                list.Count(s => s.Provider == "claude" && s.Status == "working"), list.Count(s => s.Provider == "claude" && s.Status == "waiting"),
                list.Count(s => s.Provider == "chatgpt" && s.Status == "working"), list.Count(s => s.Provider == "chatgpt" && s.Status == "waiting") };
            wanted = counts.Sum() > 0 || panelOpen;
            area = workArea;
            if (Math.Abs(scale - k) > 0.01) { k = scale; if (surf != null) { surf.Dispose(); surf = null; } }
            redraw = 0;
        }

        public Rectangle Bounds2 { get { return new Rectangle(area.Right - W, Top2, W, H); } }
        int W { get { return (int)(38 * k); } }
        int H { get { return (int)(128 * k); } }
        int Top2 { get { return area.Top + (int)(area.Height * 0.26); } }

        public void Tick(double dt)
        {
            if (!IsHandleCreated) return;
            if (!wanted)
            {
                if (shown && surf != null) { Present(surf, -32000, -32000, 0); shown = false; }
                return;
            }
            t += dt; redraw -= dt;
            if (redraw > 0 && shown) return;
            redraw = 0.06;
            if (surf == null) surf = new Surface(W + (int)(6 * k), H);
            surf.Clear();
            Draw(surf.G);
            int slide = hover ? (int)(4 * k) : 0;
            Present(surf, area.Right - W - slide, Top2, 255);
            shown = true;
        }

        void Draw(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float w = W + 12 * k, h = H - 2;
            using (var path = Renderer.Rounded(1, 1, w, h, 12 * k))
            {
                using (var b = new SolidBrush(Color.FromArgb(246, AiColors.Paper))) g.FillPath(b, path);
                using (var p = new Pen(AiColors.Edge, 1.3f * k)) g.DrawPath(p, path);
            }
            float cx = W / 2f + 1;
            DrawGroup(g, "claude", counts[0], counts[1], cx, 30 * k);
            using (var p = new Pen(AiColors.Edge, 1f)) g.DrawLine(p, 8 * k, H / 2f, W - 6 * k, H / 2f);
            DrawGroup(g, "chatgpt", counts[2], counts[3], cx, H / 2f + 30 * k);
        }

        void DrawGroup(Graphics g, string provider, int working, int waiting, float cx, float cy)
        {
            AiColors.Mark(g, provider, cx, cy - 12 * k, 7 * k);
            var font = Renderer.GetFont("Segoe UI Semibold", 13 * k, FontStyle.Regular);
            string text = working > 0 ? working.ToString() : waiting > 0 ? waiting.ToString() : "·";
            Color dot = working > 0 ? AiColors.Working : waiting > 0 ? AiColors.Waiting : AiColors.Faint;
            if (working > 0)
            {
                float pulse = (float)(0.5 + 0.5 * Math.Sin(t * 4));
                using (var b = new SolidBrush(Color.FromArgb((int)(70 * pulse), dot))) g.FillEllipse(b, cx - 11 * k, cy + 2 * k, 22 * k, 22 * k);
            }
            using (var b = new SolidBrush(dot))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, b, new RectangleF(cx - 15 * k, cy + 2 * k, 30 * k, 22 * k), fmt);
            }
            if (working > 0 && waiting > 0)
                using (var b = new SolidBrush(AiColors.Waiting)) g.FillEllipse(b, cx + 7 * k, cy + 1 * k, 6 * k, 6 * k);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; redraw = 0; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; redraw = 0; base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) onClick(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && surf != null) surf.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>The list of AI sessions, split into Claude and ChatGPT. Slides out from the right edge.</summary>
    public sealed class SessionsPanel : Form
    {
        readonly Action<string> say;
        readonly System.Windows.Forms.Timer pulse = new System.Windows.Forms.Timer { Interval = 60 };
        List<AiSession> sessions = new List<AiSession>();
        readonly List<Hit> hits = new List<Hit>();
        Hit hot;
        float k = 1;
        int scroll, contentHeight;
        Color accent = AiColors.Claude;
        double t;

        class Hit { public RectangleF R; public string Action, Id; }

        public SessionsPanel(Action<string> say)
        {
            this.say = say;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = AiColors.Paper;
            Text = "ai sessions";
            pulse.Tick += (s, e) => { t += 0.06; if (sessions.Any(x => x.Status == "working")) Invalidate(); };
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        public void SetAccent(Color c) { accent = c; }

        public void SetSessions(List<AiSession> list)
        {
            // everything that's doing something, plus the three most recent quiet ones per provider
            sessions = list.Where(s => s.Status != "idle")
                .Concat(list.Where(s => s.Status == "idle" && s.Provider == "claude").Take(3))
                .Concat(list.Where(s => s.Status == "idle" && s.Provider == "chatgpt").Take(3)).ToList();
            if (Visible) { Relayout(); Invalidate(); }
        }

        /// <summary>Open next to the edge handle on the given work area.</summary>
        public void Open(Rectangle area, Rectangle handle)
        {
            k = DeviceDpi / 96f;
            scroll = 0;
            int w = (int)(372 * k);
            Relayout();
            int h = Math.Min((int)(area.Height * 0.8), contentHeight);
            int y = Math.Max(area.Top + 8, Math.Min(area.Bottom - h - 8, handle.Top - (int)(40 * k)));
            Bounds = new Rectangle(area.Right - handle.Width - w - (int)(10 * k), y, w, h);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            Show();
            Activate();
            pulse.Start();
        }

        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Hide(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) pulse.Stop(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); base.OnKeyDown(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int max = Math.Max(0, contentHeight - ClientSize.Height);
            scroll = Math.Max(0, Math.Min(max, scroll - e.Delta / 2));
            Relayout(); Invalidate();
        }

        Font Title { get { return Renderer.GetFont(Renderer.HandFont, 22 * k, FontStyle.Bold); } }
        Font Section { get { return Renderer.GetFont(Renderer.HandFont, 16 * k, FontStyle.Bold); } }
        Font Row { get { return Renderer.GetFont("Segoe UI Semibold", 13.5f * k, FontStyle.Regular); } }
        Font Sub { get { return Renderer.GetFont("Segoe UI", 12 * k, FontStyle.Regular); } }
        Font Small { get { return Renderer.GetFont("Segoe UI", 11.5f * k, FontStyle.Italic); } }

        const int RowH = 43;

        void Relayout()
        {
            hits.Clear();
            float y = 64 * k - scroll;
            hits.Add(new Hit { R = new RectangleF(Width - 40 * k, 12 * k - scroll, 28 * k, 28 * k), Action = "close" });
            foreach (var p in new[] { "claude", "chatgpt" })
            {
                hits.Add(new Hit { R = new RectangleF(Width - 110 * k, y, 96 * k, 26 * k), Action = "new", Id = p });
                y += 34 * k;
                var rows = sessions.Where(s => s.Provider == p).ToList();
                if (rows.Count == 0) y += 30 * k;
                foreach (var s in rows)
                {
                    hits.Add(new Hit { R = new RectangleF(10 * k, y, Width - 20 * k, RowH * k), Action = "focus", Id = s.Id });
                    if (s.Kind == "tab") hits.Add(new Hit { R = new RectangleF(Width - 42 * k, y + 8 * k, 26 * k, 26 * k), Action = "closetab", Id = s.Id });
                    y += (RowH + 2) * k;
                }
                y += 12 * k;
            }
            contentHeight = (int)(y + scroll + 34 * k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var p = new Pen(AiColors.Edge, 1.5f)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

            using (var ink = new SolidBrush(AiColors.Ink))
            using (var faint = new SolidBrush(AiColors.Faint))
            using (var acc = new SolidBrush(accent))
            {
                float y = 64 * k - scroll;
                g.DrawString("ai sessions", Title, acc, 16 * k, 12 * k - scroll);
                g.DrawString("who's working in the building", Small, faint, 18 * k, 42 * k - scroll);
                g.DrawString("×", Renderer.GetFont("Segoe UI", 20 * k, FontStyle.Regular), hot != null && hot.Action == "close" ? ink : faint, Width - 38 * k, 8 * k - scroll);

                foreach (var p in new[] { "claude", "chatgpt" })
                {
                    var rows = sessions.Where(s => s.Provider == p).ToList();
                    AiColors.Mark(g, p, 24 * k, y + 13 * k, 7 * k);
                    using (var pc = new SolidBrush(AiColors.Of(p))) g.DrawString(p, Section, pc, 38 * k, y);
                    int w = rows.Count(s => s.Status == "working"), wt = rows.Count(s => s.Status == "waiting");
                    string sum = w + wt == 0 ? "" : (w > 0 ? w + " working" : "") + (w > 0 && wt > 0 ? " · " : "") + (wt > 0 ? wt + " your move" : "");
                    g.DrawString(sum, Sub, faint, 38 * k + g.MeasureString(p, Section).Width + 4 * k, y + 5 * k);
                    bool newHot = hot != null && hot.Action == "new" && hot.Id == p;
                    using (var nb = new SolidBrush(newHot ? AiColors.Of(p) : AiColors.Faint))
                        g.DrawString("+ new chat", Sub, nb, Width - 104 * k, y + 5 * k);
                    y += 34 * k;
                    if (rows.Count == 0) { g.DrawString(p == "claude" ? "no claude sessions. quiet floor." : "no chatgpt sessions. quiet floor.", Small, faint, 38 * k, y + 2 * k); y += 30 * k; }
                    foreach (var s in rows) { DrawRow(g, s, y, ink, faint); y += (RowH + 2) * k; }
                    y += 12 * k;
                }
                g.DrawString("click to jump there · ctrl+alt+a", Small, faint, 16 * k, Math.Max(y, Height - 26 * k));
            }
        }

        void DrawRow(Graphics g, AiSession s, float y, Brush ink, Brush faint)
        {
            var r = new RectangleF(10 * k, y, Width - 20 * k, RowH * k);
            bool isHot = hot != null && hot.Id == s.Id && (hot.Action == "focus" || hot.Action == "closetab");
            if (isHot) using (var hb = new SolidBrush(Color.FromArgb(18, AiColors.Of(s.Provider)))) using (var path = Renderer.Rounded(r.X, r.Y, r.Width, r.Height, 8 * k)) g.FillPath(hb, path);

            // status dot
            float dx = 28 * k, dy = y + RowH * k / 2;
            Color c = s.Status == "working" ? AiColors.Working : s.Status == "waiting" ? AiColors.Waiting : Color.FromArgb(190, 184, 190);
            if (s.Status == "working")
            {
                float pr = (float)(6 + 4 * (0.5 + 0.5 * Math.Sin(t * 5))) * k;
                using (var pb = new SolidBrush(Color.FromArgb(60, c))) g.FillEllipse(pb, dx - pr, dy - pr, pr * 2, pr * 2);
            }
            if (s.Status == "open") using (var pen = new Pen(c, 1.6f * k)) g.DrawEllipse(pen, dx - 4.5f * k, dy - 4.5f * k, 9 * k, 9 * k);
            else using (var b = new SolidBrush(c)) g.FillEllipse(b, dx - 5 * k, dy - 5 * k, 10 * k, 10 * k);

            float tx = 46 * k, tw = r.Right - tx - (s.Kind == "tab" ? 34 * k : 10 * k);
            var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(s.Title, Row, ink, new RectangleF(tx, y + 3 * k, tw, 21 * k), fmt);
            string kind = s.Kind == "code" ? "claude code" : s.Kind == "codex" ? "codex" : s.Kind == "tab" ? "tab" : "app";
            string sub = kind + " · " + s.Where + " · " + AiSessions.StatusWords(s.Status) + (s.Kind == "app" ? "" : " · " + AiSessions.Ago(s.LastActive));
            using (var sb = new SolidBrush(s.Status == "waiting" ? Color.FromArgb(170, 120, 0) : AiColors.Faint))
                g.DrawString(sub, Sub, sb, new RectangleF(tx, y + 22 * k, tw, 19 * k), fmt);
            if (s.Kind == "tab" && isHot)
                g.DrawString("×", Renderer.GetFont("Segoe UI", 17 * k, FontStyle.Regular), hot.Action == "closetab" ? ink : faint, Width - 42 * k, y + 6 * k);
        }

        Hit HitAt(Point p)
        {
            // close-tab buttons sit on top of their rows, so check them first
            return hits.FirstOrDefault(h => h.Action == "closetab" && h.R.Contains(p)) ?? hits.FirstOrDefault(h => h.R.Contains(p));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitAt(e.Location);
            if (h != hot) { hot = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hot = null; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var h = HitAt(e.Location);
            if (h == null || e.Button != MouseButtons.Left) return;
            switch (h.Action)
            {
                case "close": Hide(); break;
                case "new":
                    string provider = h.Id;
                    ThreadPool.QueueUserWorkItem(_ => AiSessions.OpenNew(provider));
                    Hide();
                    break;
                case "closetab":
                    string tabId = h.Id;
                    ThreadPool.QueueUserWorkItem(_ => AiSessions.CloseTab(tabId));
                    sessions = sessions.Where(s => s.Id != tabId).ToList();
                    Relayout(); Invalidate();
                    break;
                case "focus":
                    string id = h.Id;
                    Hide();
                    var ui = this;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string msg;
                        if (!AiSessions.Focus(id, out msg) && msg != null)
                            try { ui.BeginInvoke(new Action(() => say(msg))); } catch { }
                    });
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) pulse.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Ctrl+Alt+A toggles the sessions panel.</summary>
    sealed class SessionsHotkey : NativeWindow, IDisposable
    {
        const int Id = 0x5E55;
        readonly Action onPress;
        public readonly bool Registered;

        public SessionsHotkey(Action onPress)
        {
            this.onPress = onPress;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // message-only window
            Registered = Native.RegisterHotKey(Handle, Id, 0x0002 | 0x0001 | 0x4000 /* ctrl+alt, no repeat */, (uint)Keys.A);
            if (!Registered) DebugLog.Write("ctrl+alt+a is taken; the sessions panel is still in the menu.");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == Id) { try { onPress(); } catch (Exception ex) { DebugLog.Write("sessions hotkey: " + ex.Message); } }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { Native.UnregisterHotKey(Handle, Id); DestroyHandle(); }
        }
    }
}
