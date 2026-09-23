using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Bestie
{
    public class WidgetState
    {
        public string Id, Kind;
        public int X = int.MinValue, Y, W, H, Color;
        public bool Pinned, Open;
    }

    /// <summary>Desktop widgets: to-do, calendar, now playing, sticky notes. Little paper cards you can drag,
    /// resize and pin on top (the creature can walk on them). Their layout lives in .state\widgets.json.</summary>
    public static class WidgetManager
    {
        public static readonly string[] Kinds = { "todo", "calendar", "music", "note" };
        static Control ui;
        static Creature creature;
        static Func<AssistantModule> assistant;
        static Color accent;
        static List<WidgetState> states = new List<WidgetState>();
        static readonly Dictionary<string, WidgetForm> open = new Dictionary<string, WidgetForm>();
        static System.Windows.Forms.Timer saveTimer;

        internal static Creature Creature { get { return creature; } }
        internal static AssistantModule Assistant { get { return assistant != null ? assistant() : null; } }
        internal static Color Accent { get { return accent; } }
        /// <summary>Set by the app: the "let me see my calendar" flow (paste the secret iCal link).</summary>
        public static Action ConnectCalendar;

        public static string Label(string kind)
        {
            switch (kind) { case "todo": return "to-do"; case "calendar": return "calendar"; case "music": return "now playing"; default: return "sticky note"; }
        }

        public static void Init(Control uiControl, Creature c, Func<AssistantModule> getAssistant)
        {
            ui = uiControl; creature = c; assistant = getAssistant;
            accent = Renderer.Hsl(c.Brain.Looks.Hue, 0.55, 0.42, 255);
            try
            {
                string path = Home.State("widgets.json");
                if (File.Exists(path)) states = Json.Read<List<WidgetState>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<WidgetState>();
            }
            catch (Exception ex) { DebugLog.Write("widgets: couldn't read layout: " + ex.Message); }
            saveTimer = new System.Windows.Forms.Timer { Interval = 800 };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); SaveNow(); };
            foreach (var st in states.Where(s => s.Open).ToList()) Open(st);
        }

        public static bool IsOpen(string kind) { return open.Values.Any(w => w.State.Kind == kind && w.Visible); }

        public static void Toggle(string kind)
        {
            if (kind == "note") { NewNote(null); return; }
            if (IsOpen(kind)) Hide(kind); else Show(kind);
        }

        public static void Show(string kind)
        {
            if (kind == "note") { ShowNotes(); return; }
            var st = states.FirstOrDefault(s => s.Id == kind) ?? Add(new WidgetState { Id = kind, Kind = kind });
            st.Open = true;
            Open(st);
            SaveSoon();
        }

        public static void Hide(string kind)
        {
            foreach (var w in open.Values.Where(w => w.State.Kind == kind).ToList()) w.Close();
        }

        public static void ShowNotes()
        {
            var notes = states.Where(s => s.Kind == "note").ToList();
            if (notes.Count == 0) { NewNote(null); return; }
            foreach (var st in notes) { st.Open = true; Open(st); }
            SaveSoon();
        }

        public static string NewNote(string text)
        {
            int n = 1;
            while (states.Any(s => s.Id == "note-" + n)) n++;
            var st = Add(new WidgetState { Id = "note-" + n, Kind = "note", Color = (n - 1) % NoteWidget.Colors.Length, Open = true });
            if (!string.IsNullOrEmpty(text)) NoteWidget.Write(st.Id, text);
            Open(st);
            SaveSoon();
            return st.Id;
        }

        /// <summary>From any thread (the assistant's tools): show/hide a widget, or make a sticky note.</summary>
        public static void Request(string kind, bool show, string text)
        {
            if (ui == null || !ui.IsHandleCreated) return;
            ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (kind == "note" && show) NewNote(text);
                    else if (show) Show(kind);
                    else Hide(kind);
                }
                catch (Exception ex) { DebugLog.Write("widgets: " + ex.Message); }
            }));
        }

        static WidgetState Add(WidgetState st) { states.Add(st); return st; }

        static void Open(WidgetState st)
        {
            WidgetForm w;
            if (open.TryGetValue(st.Id, out w) && !w.IsDisposed) { w.Show(); w.Activate(); return; }
            switch (st.Kind)
            {
                case "todo": w = new TodoWidget(st); break;
                case "calendar": w = new CalendarWidget(st); break;
                case "music": w = new MusicWidget(st); break;
                case "note": w = new NoteWidget(st); break;
                default: return;
            }
            open[st.Id] = w;
            w.FormClosed += (s, e) =>
            {
                open.Remove(st.Id);
                if (!closingAll) { st.Open = false; SaveSoon(); }
            };
            w.Show();
        }

        internal static void Forget(WidgetState st) { states.Remove(st); SaveSoon(); }

        internal static Rectangle DefaultBounds(string kind, float k)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            if (creature != null)
            {
                var m = creature.World.Mons.FirstOrDefault(o => o.Primary) ?? creature.World.Mons.FirstOrDefault();
                if (m != null) area = Rectangle.FromLTRB(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom);
            }
            int w = (int)(280 * k), h;
            switch (kind) { case "todo": h = 330; break; case "calendar": h = 360; break; case "music": h = 150; break; default: w = (int)(230 * k); h = 220; break; }
            h = (int)(h * k);
            // stack down a column from the top right (leaving room for the ai-sessions tab); start a new column when full
            int gap = (int)(14 * k), x = area.Right - w - (int)(80 * k), y = area.Top + (int)(40 * k);
            var taken = open.Values.Where(o => o.Visible).Select(o => o.Bounds).ToList();
            for (int guard = 0; guard < 40; guard++)
            {
                var r = new Rectangle(x, y, w, h);
                var hit = taken.Where(t => t.IntersectsWith(Rectangle.Inflate(r, gap / 2, gap / 2))).ToList();
                if (hit.Count == 0) return r;
                y = hit.Max(t => t.Bottom) + gap;
                if (y + h > area.Bottom - gap) { y = area.Top + (int)(40 * k); x -= w + gap; if (x < area.Left) break; }
            }
            return new Rectangle(area.Right - w - (int)(80 * k), area.Top + (int)(40 * k), w, h);
        }

        internal static void SaveSoon() { if (saveTimer != null) { saveTimer.Stop(); saveTimer.Start(); } }

        static void SaveNow()
        {
            try { File.WriteAllText(Home.State("widgets.json"), Json.Write(states), Encoding.UTF8); }
            catch (Exception ex) { DebugLog.Write("widgets: couldn't save layout: " + ex.Message); }
        }

        static bool closingAll;
        public static void CloseAll()
        {
            closingAll = true;
            foreach (var w in open.Values.ToList()) { try { w.Close(); } catch { } }
            SaveNow();
        }
    }

    /// <summary>A paper card with a header you drag, a pin, a close ×, and a resize grip.</summary>
    public abstract class WidgetForm : Form
    {
        public readonly WidgetState State;
        protected float k;
        protected Rectangle CloseBtn, PinBtn, ExtraBtn;
        protected int HeaderH { get { return (int)(34 * k); } }
        protected int Pad { get { return (int)(10 * k); } }
        protected Point Hover = new Point(-1, -1);

        protected abstract string Title { get; }
        protected virtual string Subtitle { get { return null; } }
        protected virtual Color Paper { get { return Color.FromArgb(252, 248, 238); } }
        protected virtual string ExtraGlyph { get { return null; } }
        protected static readonly Color Ink = Color.FromArgb(52, 46, 60);
        protected static readonly Color Faint = Color.FromArgb(145, 136, 146);

        protected WidgetForm(WidgetState st)
        {
            State = st;
            k = DeviceDpi / 96f;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            DoubleBuffered = true;
            KeyPreview = true;
            BackColor = Paper;
            Font = new Font("Segoe UI", 9.5f);
            Text = "bestie widget: " + st.Kind;   // the creature recognizes its own widgets by this and can stand on them
            MinimumSize = new Size((int)(170 * k), (int)(110 * k));
            var d = WidgetManager.DefaultBounds(st.Kind, k);
            if (st.X == int.MinValue || st.W < 50 || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(st.X, st.Y, st.W, st.H))))
            { st.X = d.X; st.Y = d.Y; st.W = d.Width; st.H = d.Height; }
            Bounds = new Rectangle(st.X, st.Y, st.W, st.H);
            TopMost = st.Pinned;
            Padding = new Padding(Pad, HeaderH, Pad, Pad);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3, HTCAPTION = 2, HTBOTTOMRIGHT = 17;
            if (m.Msg == WM_NCLBUTTONDBLCLK) return;   // no maximizing
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST)
            {
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                int grip = (int)(16 * k);
                if (p.X >= Width - grip && p.Y >= Height - grip) m.Result = (IntPtr)HTBOTTOMRIGHT;
                else if (p.Y < HeaderH && !CloseBtn.Contains(p) && !PinBtn.Contains(p) && !ExtraBtn.Contains(p)) m.Result = (IntPtr)HTCAPTION;
            }
        }

        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); Remember(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Remember(); Invalidate(); }

        void Remember()
        {
            if (WindowState != FormWindowState.Normal || !IsHandleCreated) return;
            State.X = Left; State.Y = Top; State.W = Width; State.H = Height;
            WidgetManager.SaveSoon();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var edge = new Pen(Color.FromArgb(40, 0, 0, 0), 1)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);

            // header
            using (var b = new SolidBrush(WidgetManager.Accent))
                g.DrawString(Title, Renderer.GetFont(Renderer.HandFont, 17 * k, FontStyle.Bold), b, Pad, 6 * k);
            string sub = Subtitle;
            float titleW = g.MeasureString(Title, Renderer.GetFont(Renderer.HandFont, 17 * k, FontStyle.Bold)).Width;
            if (!string.IsNullOrEmpty(sub))
                using (var b = new SolidBrush(Faint)) g.DrawString(sub, Renderer.GetFont("Segoe UI", 11 * k, FontStyle.Italic), b, Pad + titleW + 2 * k, 11 * k);

            float s = 22 * k;
            CloseBtn = new Rectangle((int)(Width - Pad - s), (int)(6 * k), (int)s, (int)s);
            PinBtn = new Rectangle((int)(CloseBtn.Left - s - 2 * k), CloseBtn.Top, (int)s, (int)s);
            ExtraBtn = ExtraGlyph != null ? new Rectangle((int)(PinBtn.Left - s - 2 * k), CloseBtn.Top, (int)s, (int)s) : Rectangle.Empty;
            HeaderButton(g, CloseBtn, "×");
            // pin: filled when it stays on top
            bool hotPin = PinBtn.Contains(Hover);
            using (var p = new Pen(State.Pinned ? WidgetManager.Accent : (hotPin ? Ink : Faint), 1.6f * k))
            using (var b = new SolidBrush(State.Pinned ? WidgetManager.Accent : Color.Transparent))
            {
                float cx = PinBtn.Left + PinBtn.Width / 2f, cy = PinBtn.Top + PinBtn.Height / 2f - 2 * k;
                g.FillEllipse(b, cx - 4 * k, cy - 4 * k, 8 * k, 8 * k);
                g.DrawEllipse(p, cx - 4 * k, cy - 4 * k, 8 * k, 8 * k);
                g.DrawLine(p, cx, cy + 4 * k, cx, cy + 9 * k);
            }
            if (ExtraGlyph != null) HeaderButton(g, ExtraBtn, ExtraGlyph);

            PaintBody(g, new Rectangle(Pad, HeaderH, Width - Pad * 2, Height - HeaderH - Pad));

            // resize grip
            using (var b = new SolidBrush(Color.FromArgb(90, Faint)))
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j <= i; j++)
                        g.FillEllipse(b, Width - (5 + j * 4) * k, Height - (5 + (i - j) * 4) * k, 2.2f * k, 2.2f * k);
        }

        void HeaderButton(Graphics g, Rectangle r, string glyph)
        {
            bool hot = r.Contains(Hover);
            using (var b = new SolidBrush(hot ? Ink : Faint))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(glyph, Renderer.GetFont("Segoe UI", 14 * k, FontStyle.Regular), b, r, fmt);
            }
        }

        protected abstract void PaintBody(Graphics g, Rectangle body);
        protected virtual void BodyClick(Point p, MouseButtons button) { }
        protected virtual void ExtraClick() { }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Hover = e.Location;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { Hover = new Point(-1, -1); Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && CloseBtn.Contains(e.Location)) { Close(); return; }
            if (e.Button == MouseButtons.Left && PinBtn.Contains(e.Location))
            {
                State.Pinned = !State.Pinned;
                TopMost = State.Pinned;
                WidgetManager.SaveSoon();
                Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Left && ExtraBtn.Contains(e.Location)) { ExtraClick(); return; }
            if (e.Y >= HeaderH) BodyClick(e.Location, e.Button);
        }

        /// <summary>A single-line input with a grey hint, docked at the bottom.</summary>
        protected TextBox AddInput(string hint, Action<string> onEnter)
        {
            var box = new TextBox { Dock = DockStyle.Bottom, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(244, 238, 226), Font = new Font("Segoe UI", 10f) };
            var holder = new Panel { Dock = DockStyle.Bottom, Height = (int)(30 * k), Padding = new Padding((int)(8 * k), (int)(7 * k), (int)(8 * k), 0), BackColor = box.BackColor };
            holder.Controls.Add(box);
            Controls.Add(holder);
            box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, hint);
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string text = box.Text.Trim();
                if (text.Length == 0) return;
                box.Text = "";
                onEnter(text);
            };
            return box;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        protected static void Ellipsize(Graphics g, string text, Font f, Brush b, RectangleF r)
        {
            var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
            g.DrawString(text, f, b, r, fmt);
        }
    }

    // ======================================================================== to-do

    public sealed class TodoWidget : WidgetForm
    {
        List<TodoItem> items = new List<TodoItem>();
        readonly List<KeyValuePair<Rectangle, string>> rows = new List<KeyValuePair<Rectangle, string>>();   // row -> text
        int scroll;
        FileSystemWatcher watcher;
        readonly Action changed;

        protected override string Title { get { return "to-do"; } }
        protected override string Subtitle
        {
            get { int n = items.Count(i => !i.Done); return n == 0 ? "all clear" : n + " left"; }
        }

        public TodoWidget(WidgetState st) : base(st)
        {
            AddInput("+ add a thing…", text => Todos.Add(text));
            changed = () => { try { if (IsHandleCreated) BeginInvoke(new Action(Reload)); } catch { } };
            Todos.Changed += changed;
            try
            {
                watcher = new FileSystemWatcher(Path.GetDirectoryName(Todos.FilePath), Path.GetFileName(Todos.FilePath)) { EnableRaisingEvents = true };
                watcher.Changed += (s, e) => changed();
                watcher.Created += (s, e) => changed();
            }
            catch { }
            Reload();
        }

        void Reload()
        {
            items = Todos.Load().Where(i => !i.Done || (i.DoneOn.HasValue && i.DoneOn.Value == DateTime.Today)).OrderBy(i => i.Done).ToList();
            Invalidate();
        }

        protected override void PaintBody(Graphics g, Rectangle body)
        {
            rows.Clear();
            int inputH = (int)(30 * k);
            var area = new Rectangle(body.X, body.Y + (int)(2 * k), body.Width, body.Height - inputH - (int)(6 * k));
            if (items.Count == 0)
            {
                using (var b = new SolidBrush(Faint))
                    g.DrawString("nothing to do. suspicious.", Renderer.GetFont("Segoe UI", 12 * k, FontStyle.Italic), b, area.X + 4 * k, area.Y + 8 * k);
                return;
            }
            int rowH = (int)(27 * k);
            int max = Math.Max(0, items.Count * rowH - area.Height);
            scroll = Math.Max(0, Math.Min(max, scroll));
            var clip = g.Clip;
            g.SetClip(area);
            var font = Renderer.GetFont("Segoe UI", 13 * k, FontStyle.Regular);
            var doneFont = Renderer.GetFont("Segoe UI", 13 * k, FontStyle.Strikeout);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var r = new Rectangle(area.X, area.Y + i * rowH - scroll, area.Width, rowH);
                if (r.Bottom < area.Top || r.Top > area.Bottom) continue;
                rows.Add(new KeyValuePair<Rectangle, string>(r, it.Text));
                bool hot = r.Contains(Hover);
                if (hot) using (var hb = new SolidBrush(Color.FromArgb(14, WidgetManager.Accent))) g.FillRectangle(hb, r);
                float cx = r.X + 10 * k, cy = r.Y + rowH / 2f, rad = 7 * k;
                using (var p = new Pen(it.Done ? WidgetManager.Accent : Faint, 1.6f * k))
                {
                    if (it.Done)
                    {
                        using (var fb = new SolidBrush(WidgetManager.Accent)) g.FillEllipse(fb, cx - rad, cy - rad, rad * 2, rad * 2);
                        using (var w = new Pen(Color.White, 1.8f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLines(w, new[] { new PointF(cx - 3.5f * k, cy), new PointF(cx - 1 * k, cy + 2.8f * k), new PointF(cx + 3.8f * k, cy - 2.8f * k) });
                    }
                    else g.DrawEllipse(p, cx - rad, cy - rad, rad * 2, rad * 2);
                }
                using (var tb = new SolidBrush(it.Done ? Faint : Ink))
                    Ellipsize(g, it.Text, it.Done ? doneFont : font, tb, new RectangleF(r.X + 24 * k, r.Y, r.Width - 24 * k - (hot ? 22 * k : 0), r.Height));
                if (hot)
                    using (var xb = new SolidBrush(Faint))
                        g.DrawString("×", Renderer.GetFont("Segoe UI", 13 * k, FontStyle.Regular), xb, r.Right - 20 * k, r.Y + 3 * k);
            }
            g.Clip = clip;
        }

        protected override void BodyClick(Point p, MouseButtons button)
        {
            foreach (var row in rows)
            {
                if (!row.Key.Contains(p)) continue;
                var item = items.FirstOrDefault(i => i.Text == row.Value);
                if (item == null) return;
                if (p.X > row.Key.Right - 24 * k || button == MouseButtons.Right) Todos.Remove(item.Text);
                else Todos.SetDone(item.Text, !item.Done);
                return;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e) { scroll -= e.Delta / 3; Invalidate(); base.OnMouseWheel(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Todos.Changed -= changed; if (watcher != null) watcher.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ======================================================================== calendar

    public sealed class CalendarWidget : WidgetForm
    {
        List<CalEvent> events = new List<CalEvent>();
        List<AssistantReminder> reminders = new List<AssistantReminder>();
        bool connected, loading;
        string problem;
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        Rectangle connectLink;
        int scroll;

        protected override string Title { get { return "calendar"; } }
        protected override string Subtitle { get { return loading ? "checking…" : DateTime.Now.ToString("ddd MMM d").ToLowerInvariant(); } }
        protected override string ExtraGlyph { get { return "↻"; } }

        public CalendarWidget(WidgetState st) : base(st)
        {
            AddInput("+ add an event (dentist fri 3pm)", text =>
            {
                var a = WidgetManager.Assistant;
                if (a == null) return;
                a.SubmitSpokenText("add this to my calendar: " + text, new string[0]);
                if (WidgetManager.Creature != null) WidgetManager.Creature.Say("on it.", false, 1.5);
                BeginInvoke(new Action(() => { var t = new System.Windows.Forms.Timer { Interval = 15000 }; t.Tick += (s, e) => { t.Dispose(); Fetch(); }; t.Start(); }));
            });
            refresh.Tick += (s, e) => Fetch();
            refresh.Start();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Fetch(); }

        protected override void ExtraClick() { Fetch(); }

        void Fetch()
        {
            var a = WidgetManager.Assistant;
            if (a == null) return;
            loading = true; Invalidate();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<CalEvent> evs = null; string err = null; bool conn = false; List<AssistantReminder> rems = null;
                try
                {
                    var cal = a.Calendar;
                    conn = cal != null && cal.IsConnected;
                    if (conn) evs = cal.ListEvents("primary", DateTime.Today, DateTime.Today.AddDays(7), null, 30, 15000);
                    else if (IcsCalendar.Configured)
                    {
                        conn = true;
                        evs = IcsCalendar.Events(DateTime.Today, DateTime.Today.AddDays(7));
                        err = evs.Count == 0 ? IcsCalendar.LastError : null;
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { rems = a.Reminders.Pending(); } catch { }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        loading = false; connected = conn; problem = err;
                        events = evs ?? new List<CalEvent>();
                        reminders = rems ?? new List<AssistantReminder>();
                        Invalidate();
                    }));
                }
                catch { }
            });
        }

        static string DayName(DateTime d)
        {
            if (d.Date == DateTime.Today) return "today";
            if (d.Date == DateTime.Today.AddDays(1)) return "tomorrow";
            return d.ToString("dddd, MMM d").ToLowerInvariant();
        }

        protected override void PaintBody(Graphics g, Rectangle body)
        {
            int inputH = (int)(30 * k);
            var area = new Rectangle(body.X, body.Y, body.Width, body.Height - inputH - (int)(6 * k));
            var clip = g.Clip;
            g.SetClip(area);
            float y = area.Y + 2 * k - scroll;
            var dayFont = Renderer.GetFont(Renderer.HandFont, 14 * k, FontStyle.Bold);
            var timeFont = Renderer.GetFont("Segoe UI Semibold", 11.5f * k, FontStyle.Regular);
            var font = Renderer.GetFont("Segoe UI", 12.5f * k, FontStyle.Regular);
            var small = Renderer.GetFont("Segoe UI", 11.5f * k, FontStyle.Italic);
            connectLink = Rectangle.Empty;
            using (var ink = new SolidBrush(Ink))
            using (var faint = new SolidBrush(Faint))
            using (var acc = new SolidBrush(WidgetManager.Accent))
            {
                if (!connected)
                {
                    var r = new RectangleF(area.X + 2 * k, y, area.Width - 4 * k, 80 * k);
                    g.DrawString("i can't see your google calendar yet. i can still add things: type below, or just ask me.", small, faint, r);
                    y += g.MeasureString("x", small, (int)r.Width).Height * 3 + 6 * k;
                    string link = "let me see my calendar →";
                    var sz = g.MeasureString(link, font);
                    connectLink = new Rectangle((int)(area.X + 2 * k), (int)y, (int)sz.Width, (int)sz.Height);
                    g.DrawString(link, font, acc, connectLink.X, connectLink.Y);
                    y += sz.Height + 10 * k;
                }
                else if (problem != null)
                {
                    g.DrawString(problem, small, faint, new RectangleF(area.X, y, area.Width, 60 * k));
                    y += 44 * k;
                }
                else if (events.Count == 0)
                {
                    g.DrawString("nothing on the calendar this week. suspicious.", small, faint, area.X + 2 * k, y);
                    y += 26 * k;
                }
                foreach (var day in events.GroupBy(e => e.Start.Date).OrderBy(d => d.Key))
                {
                    g.DrawString(DayName(day.Key), dayFont, acc, area.X, y);
                    y += 22 * k;
                    foreach (var e in day.OrderBy(e => e.AllDay ? 0 : 1).ThenBy(e => e.Start))
                    {
                        bool past = !e.AllDay && e.End < DateTime.Now;
                        string when = e.AllDay ? "all day" : e.Start.ToString("h:mm tt").ToLowerInvariant();
                        using (var tb = new SolidBrush(past ? Faint : Ink))
                        {
                            g.DrawString(when, timeFont, past ? faint : acc, area.X + 4 * k, y + 1 * k);
                            Ellipsize(g, e.Title ?? "(no title)", font, tb, new RectangleF(area.X + 70 * k, y - 2 * k, area.Width - 72 * k, 22 * k));
                        }
                        y += 22 * k;
                    }
                    y += 4 * k;
                }
                if (reminders.Count > 0)
                {
                    g.DrawString("reminders", dayFont, acc, area.X, y);
                    y += 22 * k;
                    foreach (var r in reminders.OrderBy(r => r.DueUtc).Take(8))
                    {
                        DateTime due = r.DueUtc.ToLocalTime();
                        g.DrawString(due.Date == DateTime.Today ? due.ToString("h:mm tt").ToLowerInvariant() : due.ToString("ddd h:mm tt").ToLowerInvariant(), timeFont, acc, area.X + 4 * k, y + 1 * k);
                        Ellipsize(g, r.Text, font, ink, new RectangleF(area.X + 70 * k, y - 2 * k, area.Width - 72 * k, 22 * k));
                        y += 22 * k;
                    }
                }
            }
            g.Clip = clip;
        }

        protected override void BodyClick(Point p, MouseButtons button)
        {
            if (connectLink.Contains(p) && WidgetManager.ConnectCalendar != null) WidgetManager.ConnectCalendar();
        }

        protected override void OnMouseWheel(MouseEventArgs e) { scroll = Math.Max(0, scroll - e.Delta / 3); Invalidate(); base.OnMouseWheel(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) refresh.Dispose();
            base.Dispose(disposing);
        }
    }

    // ======================================================================== now playing

    public sealed class MusicWidget : WidgetForm
    {
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 1000 };
        Rectangle prevBtn, playBtn, nextBtn;
        Song song;

        protected override string Title { get { return "now playing"; } }

        public MusicWidget(WidgetState st) : base(st)
        {
            tick.Tick += (s, e) =>
            {
                var c = WidgetManager.Creature;
                var now = c != null ? c.NowPlaying : null;
                if ((now == null) != (song == null) || (now != null && now.Key != song.Key)) { song = now; Invalidate(); }
            };
            tick.Start();
        }

        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        static void MediaKey(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }

        protected override void PaintBody(Graphics g, Rectangle body)
        {
            using (var ink = new SolidBrush(Ink))
            using (var faint = new SolidBrush(Faint))
            using (var acc = new SolidBrush(WidgetManager.Accent))
            {
                float y = body.Y + 2 * k;
                if (song == null)
                {
                    g.DrawString("silence in the building.", Renderer.GetFont("Segoe UI", 12 * k, FontStyle.Italic), faint, body.X + 2 * k, y + 4 * k);
                }
                else
                {
                    Ellipsize(g, song.Title, Renderer.GetFont("Segoe UI Semibold", 14 * k, FontStyle.Regular), ink, new RectangleF(body.X, y, body.Width, 24 * k));
                    Ellipsize(g, song.Artist, Renderer.GetFont("Segoe UI", 12 * k, FontStyle.Regular), faint, new RectangleF(body.X, y + 22 * k, body.Width, 20 * k));
                    // its (arbitrary, stubborn) rating out of 7
                    int rating = Music.Rating(song);
                    string who = WidgetManager.Creature != null ? WidgetManager.Creature.Brain.Name.ToLowerInvariant() : "i";
                    float rx = body.X;
                    for (int i = 0; i < 7; i++)
                    {
                        using (var b = new SolidBrush(i < rating ? WidgetManager.Accent : Color.FromArgb(60, Faint)))
                            g.FillEllipse(b, rx + i * 12 * k, y + 48 * k, 8 * k, 8 * k);
                    }
                    g.DrawString(who + " says " + rating + "/7", Renderer.GetFont("Segoe UI", 10.5f * k, FontStyle.Italic), faint, rx + 88 * k, y + 43 * k);
                }
                // media keys work for spotify and most players
                float by = body.Bottom - 30 * k, cx = body.X + body.Width / 2f, s = 28 * k;
                prevBtn = new Rectangle((int)(cx - s * 1.8f), (int)by, (int)s, (int)s);
                playBtn = new Rectangle((int)(cx - s / 2), (int)by, (int)s, (int)s);
                nextBtn = new Rectangle((int)(cx + s * 0.8f), (int)by, (int)s, (int)s);
                var icons = Renderer.GetFont("Segoe MDL2 Assets", 15 * k, FontStyle.Regular);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("", icons, prevBtn.Contains(Hover) ? acc : faint, prevBtn, fmt);
                g.DrawString(song != null ? "" : "", icons, playBtn.Contains(Hover) ? acc : ink, playBtn, fmt);
                g.DrawString("", icons, nextBtn.Contains(Hover) ? acc : faint, nextBtn, fmt);
            }
        }

        protected override void BodyClick(Point p, MouseButtons button)
        {
            if (prevBtn.Contains(p)) MediaKey(0xB1);
            else if (playBtn.Contains(p)) MediaKey(0xB3);
            else if (nextBtn.Contains(p)) MediaKey(0xB0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tick.Dispose();
            base.Dispose(disposing);
        }
    }

    // ======================================================================== sticky notes

    public sealed class NoteWidget : WidgetForm
    {
        public static readonly Color[] Colors = {
            Color.FromArgb(255, 244, 178), Color.FromArgb(255, 214, 224), Color.FromArgb(212, 245, 226),
            Color.FromArgb(214, 233, 255), Color.FromArgb(232, 222, 251) };
        readonly TextBox box;
        readonly System.Windows.Forms.Timer saver = new System.Windows.Forms.Timer { Interval = 700 };

        protected override string Title { get { return "note"; } }
        protected override Color Paper { get { return Colors[Math.Abs(State.Color) % Colors.Length]; } }
        protected override string ExtraGlyph { get { return "●"; } }

        static string PathFor(string id) { Home.Init(); return Path.Combine(Home.NotesDir, "sticky-" + id.Replace("note-", "") + ".md"); }

        public static void Write(string id, string text)
        {
            try { File.WriteAllText(PathFor(id), text ?? "", Encoding.UTF8); } catch (Exception ex) { DebugLog.Write("sticky: " + ex.Message); }
        }

        public NoteWidget(WidgetState st) : base(st)
        {
            box = new TextBox
            {
                Multiline = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Paper,
                Font = new Font(Renderer.HandFont, 12.5f), ScrollBars = ScrollBars.None, AcceptsReturn = true
            };
            try { if (File.Exists(PathFor(st.Id))) box.Text = File.ReadAllText(PathFor(st.Id), Encoding.UTF8); } catch { }
            Controls.Add(box);
            box.TextChanged += (s, e) => { saver.Stop(); saver.Start(); };
            saver.Tick += (s, e) => { saver.Stop(); Write(State.Id, box.Text); };
            FormClosing += (s, e) =>
            {
                saver.Stop();
                if (box.Text.Trim().Length == 0)
                {
                    try { File.Delete(PathFor(State.Id)); } catch { }
                    WidgetManager.Forget(State);   // an empty note just disappears
                }
                else Write(State.Id, box.Text);
            };
        }

        protected override void ExtraClick()
        {
            State.Color = (State.Color + 1) % Colors.Length;
            BackColor = Paper; box.BackColor = Paper;
            WidgetManager.SaveSoon();
            Invalidate();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); if (box.Text.Length == 0) box.Focus(); }

        protected override void PaintBody(Graphics g, Rectangle body) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) saver.Dispose();
            base.Dispose(disposing);
        }
    }
}
