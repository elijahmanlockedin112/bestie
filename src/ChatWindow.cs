// ChatWindow.cs - the small chat window that pops up near the creature. C# 5 only. UI thread only.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>RichTextBox on the newer RichEdit (msftedit.dll), which draws emoji and wraps nicer.
    /// Falls back to the classic control if msftedit isn't there.</summary>
    internal sealed class ChatHistoryBox : RichTextBox
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string name);

        static bool? modern;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (modern == null)
                {
                    try { modern = LoadLibrary("msftedit.dll") != IntPtr.Zero; } catch { modern = false; }
                }
                if (modern == true) cp.ClassName = "RICHEDIT50W";
                return cp;
            }
        }
    }

    internal sealed class ChatWindow : Form
    {
        static readonly Color Cream = Color.FromArgb(255, 250, 242);
        static readonly Color Sand = Color.FromArgb(244, 233, 218);
        static readonly Color SandHover = Color.FromArgb(236, 220, 198);
        static readonly Color Ink = Color.FromArgb(52, 46, 40);
        static readonly Color Warm = Color.FromArgb(176, 84, 32);
        static readonly Color Muted = Color.FromArgb(134, 122, 110);
        static readonly Color ListeningColor = Color.FromArgb(255, 196, 160);

        readonly AssistantModule owner;
        readonly float scale;
        readonly RichTextBox history;
        readonly TextBox input;
        readonly Button micButton, camButton, gearButton, sendButton;
        readonly LinkLabel status;
        readonly Panel attachPanel;
        readonly PictureBox thumb;
        readonly Font bodyFont, boldFont, noteFont, smallFont, emojiFont;
        readonly ToolTip tips = new ToolTip();
        Action statusAction;
        bool statusHasLink;
        bool noActivate;

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
            boldFont = new Font("Segoe UI", 14f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel);
            noteFont = new Font("Segoe UI", 12.5f * this.scale, FontStyle.Italic, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 12f * this.scale, FontStyle.Regular, GraphicsUnit.Pixel);
            emojiFont = new Font("Segoe UI Emoji", 15f * this.scale, FontStyle.Regular, GraphicsUnit.Pixel);
            Font = bodyFont;
            ClientSize = new Size(S(360), S(460));
            MinimumSize = new Size(S(280), S(300));

            // history
            history = new ChatHistoryBox();
            history.Dock = DockStyle.Fill;
            history.ReadOnly = true;
            history.BorderStyle = BorderStyle.None;
            history.BackColor = Cream;
            history.ForeColor = Ink;
            history.Font = bodyFont;
            history.DetectUrls = true;
            history.ScrollBars = RichTextBoxScrollBars.Vertical;
            history.TabStop = false;
            history.LinkClicked += delegate(object s, LinkClickedEventArgs e) { owner.OpenLinkFromChat(e.LinkText); };
            var historyHost = new Panel();
            historyHost.Dock = DockStyle.Fill;
            historyHost.Padding = new Padding(S(10), S(8), S(4), S(4));
            historyHost.BackColor = Cream;
            historyHost.Controls.Add(history);

            // screenshot strip (hidden until something is attached)
            attachPanel = new Panel();
            attachPanel.Dock = DockStyle.Bottom;
            attachPanel.Height = S(58);
            attachPanel.BackColor = Sand;
            attachPanel.Padding = new Padding(S(8), S(4), S(8), S(4));
            attachPanel.Visible = false;
            thumb = new PictureBox();
            thumb.Dock = DockStyle.Left;
            thumb.Width = S(80);
            thumb.SizeMode = PictureBoxSizeMode.Zoom;
            thumb.BackColor = Sand;
            var attachLabel = new Label();
            attachLabel.Dock = DockStyle.Fill;
            attachLabel.Text = "📷 screenshot attached to your next message";
            attachLabel.TextAlign = ContentAlignment.MiddleLeft;
            attachLabel.Font = smallFont;
            attachLabel.ForeColor = Ink;
            attachLabel.Padding = new Padding(S(8), 0, 0, 0);
            var attachRemove = new LinkLabel();
            attachRemove.Dock = DockStyle.Right;
            attachRemove.AutoSize = false;
            attachRemove.Width = S(64);
            attachRemove.Text = "remove";
            attachRemove.TextAlign = ContentAlignment.MiddleRight;
            attachRemove.Font = smallFont;
            attachRemove.LinkColor = Warm;
            attachRemove.ActiveLinkColor = Warm;
            attachRemove.LinkClicked += delegate { ClearAttachment(); owner.RemoveAttachment(); SetStatus(""); };
            attachPanel.Controls.Add(attachLabel);
            attachPanel.Controls.Add(attachRemove);
            attachPanel.Controls.Add(thumb);

            // status line
            status = new LinkLabel();
            status.Dock = DockStyle.Bottom;
            status.AutoSize = false;
            status.Height = S(24);
            status.Font = smallFont;
            status.ForeColor = Muted;
            status.LinkColor = Warm;
            status.ActiveLinkColor = Warm;
            status.LinkBehavior = LinkBehavior.HoverUnderline;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Padding = new Padding(S(10), 0, S(8), 0);
            status.BackColor = Cream;
            status.LinkArea = new LinkArea(0, 0);
            status.LinkClicked += delegate { if (statusAction != null) statusAction(); };

            // input row
            var inputRow = new Panel();
            inputRow.Dock = DockStyle.Bottom;
            inputRow.Height = S(68);
            inputRow.Padding = new Padding(S(8), S(4), S(8), S(4));
            inputRow.BackColor = Cream;
            input = new TextBox();
            input.Multiline = true;
            input.AcceptsReturn = true;
            input.Dock = DockStyle.Fill;
            input.BorderStyle = BorderStyle.FixedSingle;
            input.Font = bodyFont;
            input.ScrollBars = ScrollBars.Vertical;
            input.BackColor = Color.White;
            input.ForeColor = Ink;
            input.KeyDown += OnInputKeyDown;
            sendButton = MakeButton("→", "send (enter)", boldFont);
            sendButton.Dock = DockStyle.Right;
            sendButton.Width = S(44);
            sendButton.Click += delegate { Send(); };
            var gap = new Panel();
            gap.Dock = DockStyle.Right;
            gap.Width = S(6);
            gap.BackColor = Cream;
            inputRow.Controls.Add(input);
            inputRow.Controls.Add(gap);
            inputRow.Controls.Add(sendButton);

            // toolbar
            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = S(44);
            bar.Padding = new Padding(S(8), S(2), S(8), S(6));
            bar.BackColor = Cream;
            bar.WrapContents = false;
            micButton = MakeButton("🎤", "talk to me", emojiFont);
            micButton.Click += delegate { owner.ToggleVoice(); };
            camButton = MakeButton("📷", "attach a screenshot to your next message", emojiFont);
            camButton.Click += delegate { owner.ShareScreenAndAsk(); };
            gearButton = MakeButton("⚙", "settings", emojiFont);
            gearButton.Click += delegate { owner.OpenSettings(); };
            var hint = new Label();
            hint.AutoSize = true;
            hint.Text = "enter sends · esc hides";
            hint.Font = smallFont;
            hint.ForeColor = Muted;
            hint.Margin = new Padding(S(6), S(10), 0, 0);
            bar.Controls.Add(micButton);
            bar.Controls.Add(camButton);
            bar.Controls.Add(gearButton);
            bar.Controls.Add(hint);

            // docking goes from the last-added control inwards
            Controls.Add(historyHost);
            Controls.Add(attachPanel);
            Controls.Add(status);
            Controls.Add(inputRow);
            Controls.Add(bar);
            ResumeLayout(false);
        }

        Button MakeButton(string glyph, string tip, Font font)
        {
            var b = new Button();
            b.Text = glyph;
            b.Font = font;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = SandHover;
            b.BackColor = Sand;
            b.ForeColor = Ink;
            b.Size = new Size(S(40), S(34));
            b.Margin = new Padding(0, 0, S(6), 0);
            b.TabStop = false;
            b.Cursor = Cursors.Hand;
            b.UseMnemonic = false;
            tips.SetToolTip(b, tip);
            return b;
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
            if (disposing)
            {
                bodyFont.Dispose(); boldFont.Dispose(); noteFont.Dispose(); smallFont.Dispose(); emojiFont.Dispose();
            }
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

        void Run(string text, Font f, Color c)
        {
            history.SelectionStart = history.TextLength;
            history.SelectionLength = 0;
            history.SelectionFont = f;
            history.SelectionColor = c;
            history.SelectedText = text;
        }

        void Gap() { if (history.TextLength > 0) Run("\n", smallFont, Ink); }

        void Done()
        {
            if (history.TextLength > 60000)
            {
                int cut = history.Text.IndexOf('\n', history.TextLength - 40000);
                if (cut > 0)
                {
                    history.Select(0, cut + 1);
                    history.SelectedText = "";
                }
            }
            history.SelectionStart = history.TextLength;
            history.SelectionLength = 0;
            history.ScrollToCaret();
        }

        static string Clean(string s) { return (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim(); }

        public void AppendYou(string text) { AppendYou(text, false); }

        public void AppendYou(string text, bool spoken)
        {
            Gap();
            Run(spoken ? "you (out loud): " : "you: ", boldFont, Ink);
            Run(Clean(text) + "\n", bodyFont, Ink);
            Done();
        }

        public void AppendCreature(string text)
        {
            Gap();
            Run(CreatureName + ": ", boldFont, Warm);
            Run(Clean(text) + "\n", bodyFont, Warm);
            Done();
        }

        public void AppendNote(string text)
        {
            Run("   " + Clean(text) + "\n", noteFont, Muted);
            Done();
        }

        internal string HistoryText { get { return history.Text; } }
        internal RichTextBox HistoryBoxForTests { get { return history; } }

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
        }

        public void ClearAttachment()
        {
            attachPanel.Visible = false;
            Image old = thumb.Image;
            thumb.Image = null;
            if (old != null) old.Dispose();
        }

        public void SetListening(bool on)
        {
            micButton.BackColor = on ? ListeningColor : Sand;
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
