// LockInFlash.cs - the "LOCK IN MODE ACTIVATED" moment. A dark screen over every monitor for a few seconds while
// the tabs get cleared, then it says what happened and fades out. Click-through and never takes focus, so it
// can't eat a keystroke. One flash of white at the start, nothing that strobes. UI thread only. C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Bestie
{
    internal sealed class LockInFlash
    {
        const double MinSeconds = 2.8, MaxSeconds = 7, FadeOut = 0.45;
        readonly List<LockInScreen> screens = new List<LockInScreen>();
        readonly Timer tick = new Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double doneAt = -1;   // when the summary arrived

        /// <summary>Show it on every screen now. hue is the creature's colour.</summary>
        public static LockInFlash Show(string name, double hue, bool preview)
        {
            var f = new LockInFlash();
            Color accent = Renderer.Hsl(hue, 0.85, 0.6, 255);
            foreach (Screen s in Screen.AllScreens)
            {
                var w = new LockInScreen(s, f.clock, accent, ((name ?? "bestie") + (preview ? "  ·  preview" : "")).ToUpperInvariant());
                f.screens.Add(w);
                w.Show();
            }
            f.tick.Tick += (o, e) => f.Step();
            f.tick.Start();
            return f;
        }

        /// <summary>What got done (two short lines). The screen holds a moment for it, then goes.</summary>
        public void Done(string line1, string line2)
        {
            foreach (var w in screens) { w.Line1 = line1 ?? ""; w.Line2 = line2 ?? ""; w.LinesAt = clock.Elapsed.TotalSeconds; }
            doneAt = clock.Elapsed.TotalSeconds;
        }

        void Step()
        {
            double t = clock.Elapsed.TotalSeconds;
            // leave once it's been up long enough and the summary has been readable for a beat (or it never came)
            double leave = doneAt < 0 ? MaxSeconds : Math.Max(MinSeconds, doneAt + 1.6);
            double alpha = Math.Min(1, t / 0.08) * (t < leave ? 1 : Math.Max(0, 1 - (t - leave) / FadeOut));
            foreach (var w in screens) { w.SetAlpha(alpha); w.Invalidate(); }
            if (t >= leave + FadeOut)
            {
                tick.Stop(); tick.Dispose();
                foreach (var w in screens) { try { w.Close(); w.Dispose(); } catch { } }
                screens.Clear();
            }
        }
    }

    sealed class LockInScreen : Form
    {
        public string Line1 = "", Line2 = "";
        public double LinesAt = -1;
        readonly System.Diagnostics.Stopwatch clock;
        readonly Color accent;
        readonly string label;
        readonly float k;
        static readonly Color Ink = Color.FromArgb(10, 10, 12);

        public LockInScreen(Screen s, System.Diagnostics.Stopwatch clock, Color accent, string label)
        {
            this.clock = clock; this.accent = accent; this.label = label;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = s.Bounds;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Ink;
            Text = "lock in";
            Opacity = 0.01;
            k = Math.Max(0.75f, s.Bounds.Height / 1080f);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public void SetAlpha(double a)
        {
            // kept under 1 so the window stays layered (and click-through)
            try { Opacity = Math.Max(0.01, Math.Min(0.94, a * 0.94)); } catch { }
        }

        static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }
        static double Span(double t, double from, double to) { return Ease((t - from) / (to - from)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { Draw(e.Graphics); } catch (Exception ex) { DebugLog.Write("lockin: paint: " + ex.Message); }
        }

        void Draw(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            double t = clock.Elapsed.TotalSeconds;

            // one white hit, fading to black in a fifth of a second
            double hit = Math.Max(0, 1 - t / 0.2);
            int v = (int)(10 + 245 * hit * hit);
            g.Clear(Color.FromArgb(v, v, Math.Min(255, v + 2)));

            float cx = Width / 2f, top = Height * 0.30f;
            var big = Renderer.GetFont("Bahnschrift SemiBold Condensed", 230 * k, FontStyle.Regular);
            var sub = Renderer.GetFont("Bahnschrift SemiBold SemiConden", 40 * k, FontStyle.Regular);
            var tiny = Renderer.GetFont("Bahnschrift SemiBold", 17 * k, FontStyle.Regular);
            var body = Renderer.GetFont("Bahnschrift", 27 * k, FontStyle.Regular);
            var body2 = Renderer.GetFont("Bahnschrift Light", 23 * k, FontStyle.Regular);

            // the creature's name, small, in its colour
            double a0 = Span(t, 0.12, 0.4);
            Tracked(g, label, tiny, Color.FromArgb((int)(255 * a0), accent), cx, top - 34 * k, 5 * k);

            // LOCK IN rises into place
            double a1 = Span(t, 0.06, 0.42);
            float rise = (float)((1 - a1) * 40 * k);
            SizeF bs = g.MeasureString("LOCK IN", big, PointF.Empty, StringFormat.GenericTypographic);
            using (var br = new SolidBrush(Color.FromArgb((int)(255 * a1), 246, 244, 238)))
                g.DrawString("LOCK IN", big, br, cx - bs.Width / 2, top + rise, StringFormat.GenericTypographic);

            // a bar sweeps under it, left to right
            float barY = top + bs.Height * 0.98f, barW = bs.Width * (float)Span(t, 0.22, 0.62);
            using (var br = new SolidBrush(accent)) g.FillRectangle(br, cx - bs.Width / 2, barY, barW, Math.Max(4, 7 * k));

            // MODE ACTIVATED tightens in from wide tracking
            double a2 = Span(t, 0.45, 0.85);
            Tracked(g, "MODE ACTIVATED", sub, Color.FromArgb((int)(220 * a2), 246, 244, 238), cx, barY + 30 * k, (float)((26 - 14 * a2) * k));

            // what mothball did, once it knows
            if (LinesAt >= 0)
            {
                double a3 = Span(t, LinesAt, LinesAt + 0.35);
                float y = barY + 128 * k + (float)((1 - a3) * 10 * k);
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                using (var br = new SolidBrush(Color.FromArgb((int)(235 * a3), 246, 244, 238)))
                    g.DrawString(Line1, body, br, new RectangleF(0, y, Width, 44 * k), fmt);
                using (var br = new SolidBrush(Color.FromArgb((int)(150 * a3), 246, 244, 238)))
                    g.DrawString(Line2, body2, br, new RectangleF(0, y + 46 * k, Width, 40 * k), fmt);
            }
            else if (t > 0.9)
            {
                // still deciding: three quiet dots
                int on = (int)(t * 3) % 3;
                for (int i = 0; i < 3; i++)
                    using (var br = new SolidBrush(Color.FromArgb(i == on ? 200 : 70, 246, 244, 238)))
                        g.FillEllipse(br, cx - 22 * k + i * 18 * k, barY + 140 * k, 7 * k, 7 * k);
            }
        }

        /// <summary>Text drawn letter by letter with extra space between, centred on cx.</summary>
        static void Tracked(Graphics g, string s, Font f, Color c, float cx, float y, float gap)
        {
            var fmt = StringFormat.GenericTypographic;
            var widths = new float[s.Length];
            float total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                widths[i] = s[i] == ' ' ? f.Size * 0.3f : g.MeasureString(s[i].ToString(), f, PointF.Empty, fmt).Width;
                total += widths[i] + (i < s.Length - 1 ? gap : 0);
            }
            float x = cx - total / 2;
            using (var br = new SolidBrush(c))
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] != ' ') g.DrawString(s[i].ToString(), f, br, x, y, fmt);
                    x += widths[i] + gap;
                }
        }
    }
}
