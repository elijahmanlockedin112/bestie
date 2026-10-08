// ShopForm.cs - the shop: a big live preview of the creature trying on whatever you point at (jumping rope, reading, eating
// toast), the crumbs you've got, and one row per item with its price and a buy / wear button. Custom painted, in the
// settings window's warm paper colors. UI thread. C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace Bestie
{
    internal sealed class ShopForm : Form
    {
        static readonly Color Cream = Color.FromArgb(255, 250, 242), Ink = Color.FromArgb(52, 46, 40), Warm = Color.FromArgb(176, 84, 32),
            Muted = Color.FromArgb(134, 122, 110), Rule = Color.FromArgb(234, 224, 208), Hover = Color.FromArgb(250, 243, 232),
            Picked = Color.FromArgb(246, 235, 218), Bad = Color.FromArgb(190, 50, 40), Floor = Color.FromArgb(243, 233, 216);

        const int W = 540, PreviewH = 240, InfoH = 46, RowH = 60, Pad = 16, FootH = 22;

        readonly Brain brain;
        readonly Creature preview;
        readonly float k;
        readonly Timer timer;
        readonly Font nameFont, blurbFont, buttonFont, smallFont, balanceFont;
        int picked = -1, hover = -1;
        string flash; bool flashBad; double flashUntil;

        int S(double px) { return (int)Math.Round(px * k); }

        public ShopForm(Brain brain, float scale)
        {
            this.brain = brain;
            k = scale < 0.5f || scale > 5f ? 1f : scale;
            Text = brain.Name + "'s shop";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Cream;
            DoubleBuffered = true;
            KeyPreview = true;
            ClientSize = new Size(S(W), S(PreviewH + InfoH + Shop.Items.Length * RowH + FootH + Pad));
            nameFont = new Font("Segoe UI Semibold", 14f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            blurbFont = new Font("Segoe UI", 12.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            buttonFont = new Font("Segoe UI Semibold", 13f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            smallFont = new Font("Segoe UI", 12f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            balanceFont = Renderer.GetFont(Renderer.HandFont, 30f * k, FontStyle.Bold);

            preview = new Creature(brain, 0) { S = 2.6 * k };
            preview.Pose = Pose.Stand; preview.LookX = 0.35; preview.LookY = 0.25;

            timer = new Timer { Interval = 33 };
            timer.Tick += delegate { Invalidate(new Rectangle(0, 0, ClientSize.Width, S(PreviewH))); };
            timer.Start();
            Crumbs.Changed += OnChanged;
            FormClosed += delegate { Crumbs.Changed -= OnChanged; timer.Stop(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); nameFont.Dispose(); blurbFont.Dispose(); buttonFont.Dispose(); smallFont.Dispose(); }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OnChanged() { if (!IsDisposed) { ApplyPreview(); Invalidate(); } }

        // ------------------------------------------------------------------ layout

        Rectangle RowRect(int i) { return new Rectangle(S(Pad), S(PreviewH + InfoH + i * RowH), S(W - 2 * Pad), S(RowH - 4)); }
        Rectangle ButtonRect(int i) { Rectangle r = RowRect(i); return new Rectangle(r.Right - S(100), r.Top + (r.Height - S(30)) / 2, S(92), S(30)); }

        enum Act { Buy, Wear, TakeOff, Short, Owned }

        Act StateOf(ShopItem item, out string label)
        {
            bool owned = brain.Owned.Contains(item.Id) && item.Kind != "snack";
            if (owned && item.Kind == "wear")
            {
                string on;
                bool worn = brain.Wearing.TryGetValue(item.Slot, out on) && on == item.Id;
                label = worn ? "take off" : "wear";
                return worn ? Act.TakeOff : Act.Wear;
            }
            if (owned) { label = "yours"; return Act.Owned; }
            if (brain.Crumbs < item.Price) { label = "need " + (item.Price - brain.Crumbs); return Act.Short; }
            label = "buy";
            return Act.Buy;
        }

        // ------------------------------------------------------------------ the preview

        void ApplyPreview()
        {
            preview.Pose = Pose.Stand; preview.Face = Face.Normal; preview.Bites = 0;
            preview.PreviewWear = null;
            if (picked < 0) return;
            ShopItem item = Shop.Items[picked];
            switch (item.Id)
            {
                case "jumprope": preview.Pose = Pose.JumpRope; preview.Face = Face.Happy; break;
                case "book": preview.Pose = Pose.Read; preview.Face = Face.Content; break;
                case "toast": preview.Pose = Pose.Eat; preview.Face = Face.Happy; preview.Bites = 1; break;
                default:
                    if (item.Kind == "wear")
                    {
                        // try it on over what it's wearing now
                        var wear = new Dictionary<string, string>(brain.Wearing);
                        string on;
                        if (wear.TryGetValue(item.Slot, out on) && on == item.Id) wear.Remove(item.Slot); else wear[item.Slot] = item.Id;
                        preview.PreviewWear = wear;
                        preview.Face = Face.Happy;
                    }
                    break;
            }
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width;

            // preview: a little stage with a rug
            using (var floor = new SolidBrush(Floor)) g.FillRectangle(floor, 0, S(PreviewH - 40), w, S(40));
            using (var rug = new SolidBrush(Color.FromArgb(40, 120, 80, 40))) g.FillEllipse(rug, w / 2 - S(70), S(PreviewH - 44), S(140), S(18));
            preview.Blink = 0;
            Renderer.DrawCreature(g, preview, w / 2f, S(PreviewH - 32));

            // balance, top left
            DrawCrumb(g, S(Pad + 10), S(Pad + 20), 1.6f * k);
            using (var ink = new SolidBrush(Ink)) g.DrawString(brain.Crumbs.ToString(), balanceFont, ink, S(Pad + 24), S(Pad - 2));
            using (var muted = new SolidBrush(Muted)) g.DrawString(Shop.Currency, smallFont, muted, S(Pad + 26), S(Pad + 34));

            if (flash != null && Clock.T < flashUntil)
            {
                SizeF sz = g.MeasureString(flash, smallFont);
                var r = new RectangleF(w / 2f - sz.Width / 2 - S(10), S(14), sz.Width + S(20), sz.Height + S(8));
                using (var p = Renderer.Rounded(r.X, r.Y, r.Width, r.Height, S(10)))
                using (var fill = new SolidBrush(Color.White))
                using (var edge = new Pen(flashBad ? Bad : Warm, 1.5f * k))
                {
                    g.FillPath(fill, p); g.DrawPath(edge, p);
                }
                using (var ink = new SolidBrush(flashBad ? Bad : Ink)) g.DrawString(flash, smallFont, ink, r.X + S(10), r.Y + S(4));
            }

            // how to earn
            using (var muted = new SolidBrush(Muted))
                g.DrawString("focus blocks pay best (about 23 per 90 minutes). chatting and petting earn a few a day.",
                    smallFont, muted, new RectangleF(S(Pad), S(PreviewH + 10), w - S(2 * Pad), S(InfoH - 10)));
            using (var rule = new Pen(Rule, 1)) g.DrawLine(rule, S(Pad), S(PreviewH + InfoH - 6), w - S(Pad), S(PreviewH + InfoH - 6));

            for (int i = 0; i < Shop.Items.Length; i++) DrawRow(g, i);

            using (var muted = new SolidBrush(Muted))
            {
                string worn = brain.Wearing.Count == 0 ? "wearing nothing. bold." : "wearing: " + string.Join(", ", brain.Wearing.Values.Select(id => Shop.Find(id)).Where(x => x != null).Select(x => x.Name).ToArray());
                g.DrawString(worn, smallFont, muted, S(Pad), ClientSize.Height - S(FootH + 6));
            }
        }

        void DrawRow(Graphics g, int i)
        {
            ShopItem item = Shop.Items[i];
            Rectangle r = RowRect(i);
            if (i == picked || i == hover)
                using (var p = Renderer.Rounded(r.X, r.Y, r.Width, r.Height, S(10)))
                using (var fill = new SolidBrush(i == picked ? Picked : Hover)) g.FillPath(fill, p);

            Renderer.DrawItemIcon(g, item.Id, r.X + S(28), r.Y + r.Height / 2f, 0.85f * k, brain.Looks);

            float tx = r.X + S(58);
            string label;
            Act act = StateOf(item, out label);
            Rectangle b = ButtonRect(i);
            float textW = b.Left - S(64) - tx;
            using (var ink = new SolidBrush(Ink)) g.DrawString(item.Name, nameFont, ink, tx, r.Y + S(9));
            using (var muted = new SolidBrush(Muted))
            using (var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(item.Blurb, blurbFont, muted, new RectangleF(tx, r.Y + S(31), b.Left - S(8) - tx, S(18)), fmt);

            // price, next to the button
            if (act == Act.Buy || act == Act.Short)
            {
                string price = item.Price.ToString();
                SizeF ps = g.MeasureString(price, nameFont);
                float px = b.Left - S(10) - ps.Width;
                using (var ink = new SolidBrush(act == Act.Short ? Muted : Ink)) g.DrawString(price, nameFont, ink, px, r.Y + S(9));
                DrawCrumb(g, px - S(9), r.Y + S(20), 0.9f * k);
            }

            // the button
            bool filled = act == Act.Buy || act == Act.Wear;
            Color edge = act == Act.Short || act == Act.Owned ? Rule : Warm;
            using (var p = Renderer.Rounded(b.X, b.Y, b.Width, b.Height, S(15)))
            {
                if (filled) using (var fill = new SolidBrush(Warm)) g.FillPath(fill, p);
                else using (var fill = new SolidBrush(Color.White)) g.FillPath(fill, p);
                using (var pen = new Pen(edge, 1.5f * k)) g.DrawPath(pen, p);
            }
            Color text = filled ? Color.White : act == Act.TakeOff ? Warm : Muted;
            using (var tb = new SolidBrush(text))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(label, buttonFont, tb, b, fmt);
        }

        static void DrawCrumb(Graphics g, float cx, float cy, float scale)
        {
            using (var crust = new SolidBrush(Color.FromArgb(255, 214, 150, 72)))
            using (var inner = new SolidBrush(Color.FromArgb(255, 246, 214, 150)))
            using (var edge = new Pen(Color.FromArgb(255, 120, 70, 30), 1.1f))
            {
                g.FillEllipse(crust, cx - 5 * scale, cy - 4 * scale, 10 * scale, 8 * scale);
                g.FillEllipse(inner, cx - 2.5f * scale, cy - 2 * scale, 4 * scale, 3 * scale);
                g.DrawEllipse(edge, cx - 5 * scale, cy - 4 * scale, 10 * scale, 8 * scale);
            }
        }

        // ------------------------------------------------------------------ mouse

        int RowAt(Point p)
        {
            for (int i = 0; i < Shop.Items.Length; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = RowAt(e.Location);
            string ignored;
            bool onButton = h >= 0 && ButtonRect(h).Contains(e.Location) && StateOf(Shop.Items[h], out ignored) != Act.Short && StateOf(Shop.Items[h], out ignored) != Act.Owned;
            Cursor = onButton ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = RowAt(e.Location);
            if (i < 0) return;
            ShopItem item = Shop.Items[i];
            if (picked != i) { picked = i; ApplyPreview(); Invalidate(); }
            if (!ButtonRect(i).Contains(e.Location)) return;
            string label, problem = null;
            switch (StateOf(item, out label))
            {
                case Act.Buy:
                    problem = Crumbs.Buy(item);
                    if (problem == null) Flash(item.Kind == "snack" ? "toast acquired. it's eating it." : "it's yours" + (item.Kind == "wear" ? ", and it's on." : "."), false);
                    break;
                case Act.Wear: case Act.TakeOff:
                    problem = Crumbs.ToggleWear(item);
                    break;
                case Act.Short:
                    problem = "that's " + item.Price + " crumbs. focus blocks pay best.";
                    break;
            }
            if (problem != null) Flash(problem, true);
            ApplyPreview();
            Invalidate();
        }

        void Flash(string text, bool bad) { flash = text; flashBad = bad; flashUntil = Clock.T + 3; }

        /// <summary>Tests: the window as it paints, with this item picked (-1 = none), saved as a PNG.</summary>
        internal static void RenderTo(Brain brain, float scale, int pick, int hoverRow, string path)
        {
            using (var f = new ShopForm(brain, scale))
            {
                f.timer.Stop();
                f.picked = pick; f.hover = hoverRow;
                f.ApplyPreview();
                var h = f.Handle;
                using (var bmp = new Bitmap(f.ClientSize.Width, f.ClientSize.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) { g.Clear(Cream); f.OnPaint(new PaintEventArgs(g, new Rectangle(Point.Empty, f.ClientSize))); }
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
    }
}
