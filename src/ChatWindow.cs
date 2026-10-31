// ChatWindow.cs - the chat window that pops up near the creature: the history (ChatView.cs draws it), a status line,
// a screenshot card when one is attached, and the compose box (grows as you type; screenshot, mic and settings icons;
// a send button that lights up when there's something to send). C# 5 only. UI thread only.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>A round icon button with a drawn line icon (no emoji, no images). Kinds: mic, camera, settings, send.</summary>
    internal sealed class IconButton : Control
    {
        static readonly Color Ink = Color.FromArgb(52, 46, 40);
        static readonly Color Warm = Color.FromArgb(176, 84, 32);
        static readonly Color Hover = Color.FromArgb(244, 233, 218);
        static readonly Color Off = Color.FromArgb(226, 211, 190);
        readonly string kind;
        readonly float scale;
        bool hover;
        public bool Lit;          // mic: listening; send: there's something to send

        public IconButton(string kind, float scale)
        {
            this.kind = kind;
            this.scale = scale;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float s = scale, w = Width, h = Height, cx = w / 2f, cy = h / 2f;
            bool send = kind == "send";
            Color fill = send ? (Lit ? (hover ? Color.FromArgb(150, 68, 22) : Warm) : Off) : Lit ? Warm : hover ? Hover : Color.Transparent;
            if (fill.A > 0) using (var br = new SolidBrush(fill)) g.FillEllipse(br, 1, 1, w - 2, h - 2);
            Color line = send || Lit ? Color.White : Ink;
            using (var pen = new Pen(line, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (kind)
                {
                    case "mic":
                        {
                            var cap = new RectangleF(cx - 3.5f * s, cy - 8f * s, 7f * s, 11f * s);
                            using (var p = Capsule(cap)) g.DrawPath(pen, p);
                            g.DrawArc(pen, cx - 6.5f * s, cy - 5f * s, 13f * s, 11f * s, 0, 180);
                            g.DrawLine(pen, cx, cy + 6f * s, cx, cy + 8.5f * s);
                            g.DrawLine(pen, cx - 3.5f * s, cy + 8.5f * s, cx + 3.5f * s, cy + 8.5f * s);
                            break;
                        }
                    case "camera":
                        {
                            var body = new RectangleF(cx - 8.5f * s, cy - 5f * s, 17f * s, 12f * s);
                            using (var p = RoundRect(body, 2.5f * s)) g.DrawPath(pen, p);
                            g.DrawLines(pen, new[] { new PointF(cx - 4f * s, cy - 5f * s), new PointF(cx - 2.5f * s, cy - 7.5f * s), new PointF(cx + 2.5f * s, cy - 7.5f * s), new PointF(cx + 4f * s, cy - 5f * s) });
                            g.DrawEllipse(pen, cx - 3.2f * s, cy - 2.2f * s, 6.4f * s, 6.4f * s);
                            break;
                        }
                    case "settings":
                        {
                            // sliders
                            float[] ys = { cy - 5f * s, cy, cy + 5f * s };
                            float[] knobs = { cx + 3f * s, cx - 3.5f * s, cx + 1f * s };
                            for (int i = 0; i < 3; i++)
                            {
                                g.DrawLine(pen, cx - 8f * s, ys[i], cx + 8f * s, ys[i]);
                                using (var br = new SolidBrush(fill.A > 0 ? fill : BackColor)) g.FillEllipse(br, knobs[i] - 2.4f * s, ys[i] - 2.4f * s, 4.8f * s, 4.8f * s);
                                g.DrawEllipse(pen, knobs[i] - 2.4f * s, ys[i] - 2.4f * s, 4.8f * s, 4.8f * s);
                            }
                            break;
                        }
                    case "send":
                        g.DrawLine(pen, cx, cy + 6f * s, cx, cy - 6f * s);
                        g.DrawLines(pen, new[] { new PointF(cx - 5f * s, cy - 1f * s), new PointF(cx, cy - 6f * s), new PointF(cx + 5f * s, cy - 1f * s) });
                        break;
                }
            }
        }

        static GraphicsPath Capsule(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = r.Width;
            p.AddArc(r.X, r.Y, d, d, 180, 180);
            p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
            p.CloseFigure();
            return p;
        }

        internal static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class ChatWindow : Form
    {
        static readonly Color Cream = Color.FromArgb(255, 250, 242);
        static readonly Color Sand = Color.FromArgb(244, 233, 218);
        static readonly Color SandLine = Color.FromArgb(226, 211, 190);
        static readonly Color Ink = Color.FromArgb(52, 46, 40);
        static readonly Color Warm = Color.FromArgb(176, 84, 32);
        static readonly Color Muted = Color.FromArgb(134, 122, 110);

        readonly AssistantModule owner;
        readonly float scale;
        readonly ChatView history;
        readonly TextBox input;
        readonly Label placeholder;
        readonly IconButton micButton, camButton, gearButton, sendButton;
        readonly Panel composer, attachPanel;
        readonly LinkLabel status;
        readonly PictureBox thumb;
        readonly Font bodyFont, smallFont, smallBold;
        readonly ToolTip tips = new ToolTip();
        Action statusAction;
        bool statusHasLink;
        bool noActivate;
        bool inputFocused;

        public string CreatureName;

        int S(double px) { return (int)Math.Round(px * scale); }

        public ChatWindow(AssistantModule owner, string creatureName, float scale)
        {
            this.owner = owner;
            this.scale = scale < 0.5f || scale > 5f ? 1f : scale;
            CreatureName = string.IsNullOrEmpty(creatureName) ? "bestie" : creatureName;

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = CreatureName;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            BackColor = Cream;
            bodyFont = new Font("Segoe UI", 14f * this.scale, FontStyle.Regular, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 12f * this.scale, FontStyle.Regular, GraphicsUnit.Pixel);
            smallBold = new Font("Segoe UI", 12f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel);
            Font = bodyFont;
            ClientSize = new Size(S(420), S(560));
            MinimumSize = new Size(S(300), S(340));

            // history
            history = new ChatView(this.scale);
            history.Dock = DockStyle.Fill;
            history.LinkClicked += delegate(string url) { owner.OpenLinkFromChat(url); };

            // screenshot card (hidden until something is attached)
            attachPanel = new Panel();
            attachPanel.Dock = DockStyle.Bottom;
            attachPanel.Height = S(64);
            attachPanel.BackColor = Cream;
            attachPanel.Padding = new Padding(S(12), S(4), S(12), S(4));
            attachPanel.Visible = false;
            attachPanel.Paint += delegate(object o, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(S(12), S(4), attachPanel.Width - S(24), attachPanel.Height - S(8));
                using (var p = IconButton.RoundRect(r, S(10)))
                {
                    using (var br = new SolidBrush(Sand)) e.Graphics.FillPath(br, p);
                    using (var pen = new Pen(SandLine)) e.Graphics.DrawPath(pen, p);
                }
            };
            thumb = new PictureBox();
            thumb.SizeMode = PictureBoxSizeMode.Zoom;
            thumb.BackColor = Sand;
            thumb.SetBounds(S(20), S(10), S(72), S(44));
            var attachLabel = new Label();
            attachLabel.AutoSize = false;
            attachLabel.Text = "screenshot goes with your next message";
            attachLabel.TextAlign = ContentAlignment.MiddleLeft;
            attachLabel.Font = smallFont;
            attachLabel.ForeColor = Ink;
            attachLabel.BackColor = Sand;
            attachLabel.SetBounds(S(100), S(10), S(200), S(44));
            attachLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            var attachRemove = new LinkLabel();
            attachRemove.AutoSize = false;
            attachRemove.Text = "remove";
            attachRemove.TextAlign = ContentAlignment.MiddleRight;
            attachRemove.Font = smallBold;
            attachRemove.LinkColor = Warm;
            attachRemove.ActiveLinkColor = Warm;
            attachRemove.LinkBehavior = LinkBehavior.HoverUnderline;
            attachRemove.BackColor = Sand;
            attachRemove.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            attachRemove.LinkClicked += delegate { ClearAttachment(); owner.RemoveAttachment(); SetStatus(""); };
            attachPanel.Controls.Add(thumb);
            attachPanel.Controls.Add(attachLabel);
            attachPanel.Controls.Add(attachRemove);
            attachPanel.Resize += delegate
            {
                attachRemove.SetBounds(attachPanel.Width - S(90), S(10), S(70), S(44));
                attachLabel.Width = Math.Max(S(60), attachPanel.Width - S(100) - S(96));
            };

            // status line
            status = new LinkLabel();
            status.Dock = DockStyle.Bottom;
            status.AutoSize = false;
            status.Height = S(22);
            status.Font = smallFont;
            status.ForeColor = Muted;
            status.LinkColor = Warm;
            status.ActiveLinkColor = Warm;
            status.LinkBehavior = LinkBehavior.HoverUnderline;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Padding = new Padding(S(14), 0, S(8), 0);
            status.BackColor = Cream;
            status.LinkArea = new LinkArea(0, 0);
            status.LinkClicked += delegate { if (statusAction != null) statusAction(); };

            // the compose box: the text on top, the icons under it
            composer = new Panel();
            composer.Dock = DockStyle.Bottom;
            composer.BackColor = Cream;
            composer.Padding = new Padding(S(12), S(4), S(12), S(12));
            composer.Paint += PaintComposer;
            input = new TextBox();
            input.Multiline = true;
            input.AcceptsReturn = true;
            input.BorderStyle = BorderStyle.None;
            input.Font = bodyFont;
            input.ScrollBars = ScrollBars.None;
            input.BackColor = Color.White;
            input.ForeColor = Ink;
            input.WordWrap = true;
            input.KeyDown += OnInputKeyDown;
            input.TextChanged += delegate { Grow(); UpdateSend(); };
            input.GotFocus += delegate { inputFocused = true; composer.Invalidate(); };
            input.LostFocus += delegate { inputFocused = false; composer.Invalidate(); };
            placeholder = new Label();
            placeholder.AutoSize = false;
            placeholder.Font = bodyFont;
            placeholder.ForeColor = Color.FromArgb(170, 158, 144);
            placeholder.BackColor = Color.White;
            placeholder.Text = "ask " + CreatureName + " anything…";
            placeholder.Cursor = Cursors.IBeam;
            placeholder.Click += delegate { FocusInput(); };
            camButton = new IconButton("camera", this.scale);
            micButton = new IconButton("mic", this.scale);
            gearButton = new IconButton("settings", this.scale);
            sendButton = new IconButton("send", this.scale);
            camButton.Click += delegate { owner.ShareScreenAndAsk(); };
            micButton.Click += delegate { owner.ToggleVoice(); };
            gearButton.Click += delegate { owner.OpenSettings(); };
            sendButton.Click += delegate { Send(); };
            tips.SetToolTip(camButton, "attach a screenshot to your next message");
            tips.SetToolTip(gearButton, "settings");
            tips.SetToolTip(sendButton, "send (enter) · new line: shift+enter");
            composer.Controls.Add(input);
            composer.Controls.Add(placeholder);
            composer.Controls.Add(camButton);
            composer.Controls.Add(micButton);
            composer.Controls.Add(gearButton);
            composer.Controls.Add(sendButton);
            composer.Resize += delegate { LayoutComposer(); };
            placeholder.BringToFront();

            // docking goes from the last-added control inwards
            Controls.Add(history);
            Controls.Add(attachPanel);
            Controls.Add(status);
            Controls.Add(composer);
            ResumeLayout(false);
            Grow();
            UpdateSend();
        }

        // ------------------------------------------------------------------ the compose box

        int lines = 1;
        const int MaxLines = 6;

        /// <summary>The box grows with what's typed, up to six lines (then it scrolls).</summary>
        void Grow()
        {
            int count = 1;
            if (input.IsHandleCreated && input.TextLength > 0)
                count = Math.Max(1, input.GetLineFromCharIndex(input.TextLength) + 1);
            count = Math.Min(MaxLines, count);
            input.ScrollBars = count >= MaxLines ? ScrollBars.Vertical : ScrollBars.None;
            int lineH = bodyFont.Height;
            int h = S(12) + count * lineH + S(8) + S(38) + S(16);
            if (h != composer.Height || count != lines) { lines = count; composer.Height = h; LayoutComposer(); }
            placeholder.Visible = input.TextLength == 0;
        }

        Rectangle BoxRect { get { return new Rectangle(S(12), S(4), composer.Width - S(24), composer.Height - S(16)); } }

        void LayoutComposer()
        {
            Rectangle box = BoxRect;
            int lineH = bodyFont.Height;
            input.SetBounds(box.X + S(14), box.Y + S(10), box.Width - S(28), lines * lineH + S(6));
            placeholder.SetBounds(input.Left + S(1), input.Top, input.Width, lineH + S(4));
            int b = S(34), iy = box.Bottom - b - S(6);
            camButton.SetBounds(box.X + S(6), iy, b, b);
            micButton.SetBounds(box.X + S(6) + b + S(2), iy, b, b);
            gearButton.SetBounds(box.X + S(6) + (b + S(2)) * 2, iy, b, b);
            sendButton.SetBounds(box.Right - b - S(8), iy, b, b);
            composer.Invalidate();
        }

        void PaintComposer(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle box = BoxRect;
            // a soft lift under the box, then the box; its edge warms up while you're typing
            using (var p = IconButton.RoundRect(new RectangleF(box.X, box.Y + S(2), box.Width, box.Height), S(16)))
            using (var br = new SolidBrush(Color.FromArgb(18, 120, 80, 40))) g.FillPath(br, p);
            using (var p = IconButton.RoundRect(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), S(16)))
            {
                using (var br = new SolidBrush(Color.White)) g.FillPath(br, p);
                using (var pen = new Pen(inputFocused ? Color.FromArgb(214, 150, 108) : SandLine, inputFocused ? 1.6f * scale : 1f)) g.DrawPath(pen, p);
            }
        }

        void UpdateSend()
        {
            bool lit = input.Text.Trim().Length > 0 || attachPanel.Visible;
            if (sendButton.Lit != lit) { sendButton.Lit = lit; sendButton.Invalidate(); }
        }

        protected override bool ShowWithoutActivation { get { return noActivate; } }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !owner.IsDisposed)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tips.Dispose();
                if (thumb.Image != null) { thumb.Image.Dispose(); thumb.Image = null; }
            }
            base.Dispose(disposing);
            if (disposing) { bodyFont.Dispose(); smallFont.Dispose(); smallBold.Dispose(); }
        }

        void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Send();
            }
            else if (e.KeyCode == Keys.A && e.Control)
            {
                input.SelectAll();
                e.SuppressKeyPress = true;
            }
        }

        void Send()
        {
            string t = input.Text;
            if (t.Trim().Length == 0 && !attachPanel.Visible) return;
            input.Clear();
            owner.SubmitFromChat(t);
        }

        // ------------------------------------------------------------------ history

        public void AppendYou(string text) { AppendYou(text, false); }

        public void AppendYou(string text, bool spoken)
        {
            history.Add(spoken ? "you (out loud)" : "you", null, Clean(text));
        }

        public void AppendCreature(string text) { history.Add("creature", CreatureName, Clean(text)); }

        public void AppendNote(string text) { history.Add("note", null, Clean(text)); }

        static string Clean(string s) { return (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim(); }

        internal string HistoryText { get { return history.PlainText; } }

        /// <summary>Checks: the whole history drawn at this width.</summary>
        internal Bitmap RenderHistory(int width) { return history.RenderAll(width); }

        // ------------------------------------------------------------------ status, attachment, mic

        public void SetStatus(string text)
        {
            statusAction = null;
            statusHasLink = false;
            status.Text = text ?? "";
            status.LinkArea = new LinkArea(0, 0);
        }

        public void SetStatusLink(string prefix, string link, Action action)
        {
            status.Text = prefix + link;
            status.LinkArea = new LinkArea(prefix.Length, link.Length);
            statusAction = action;
            statusHasLink = true;
        }

        public bool StatusHasLink { get { return statusHasLink; } }
        internal string StatusText { get { return status.Text; } }
        internal bool HasAttachment { get { return attachPanel.Visible; } }

        public void SetAttachment(Image img)
        {
            Image old = thumb.Image;
            thumb.Image = img;
            if (old != null && old != img) old.Dispose();
            attachPanel.Visible = true;
            UpdateSend();
        }

        public void ClearAttachment()
        {
            attachPanel.Visible = false;
            Image old = thumb.Image;
            thumb.Image = null;
            if (old != null) old.Dispose();
            UpdateSend();
        }

        public void SetListening(bool on)
        {
            micButton.Lit = on;
            micButton.Invalidate();
            tips.SetToolTip(micButton, on ? "listening… click to stop" : MicTip());
        }

        string MicTip()
        {
            string hk = owner.VoiceHotkeyText;
            return "talk to me" + (hk.Length > 0 ? " (" + hk.ToLowerInvariant() + ")" : "");
        }

        // ------------------------------------------------------------------ placement

        /// <summary>Above the creature (below it if there's no room), clamped to the monitor's work area.</summary>
        public void PlaceNear(Point feet, Rectangle area)
        {
            int w = Width, h = Height;
            int x = feet.X - w / 2;
            int y = feet.Y - h - S(100);
            if (y < area.Top) y = feet.Y + S(16);
            x = Math.Max(area.Left, Math.Min(x, area.Right - w));
            y = Math.Max(area.Top, Math.Min(y, area.Bottom - h));
            Location = new Point(x, y);
        }

        public void ShowAndFocus()
        {
            tips.SetToolTip(micButton, MicTip());
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            FocusInput();
        }

        /// <summary>Re-show after hiding for a screenshot; only takes focus if it had it.</summary>
        public void ShowAgain(bool activate)
        {
            if (activate) { ShowAndFocus(); return; }
            noActivate = true;
            try { Show(); }
            finally { noActivate = false; }
        }

        public void FocusInput()
        {
            input.Focus();
            input.SelectionStart = input.TextLength;
        }
    }
}
