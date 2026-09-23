using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>The creature's diary: what it has learned about the desktop and about you, in its own words.</summary>
    public sealed class DiaryForm : Form
    {
        readonly DiaryPage page;
        readonly Timer refresh = new Timer { Interval = 2500 };

        public DiaryForm(Creature c)
        {
            float k = DeviceDpi / 96f;
            Text = c.Brain.Name + "'s diary";
            ClientSize = new Size((int)(560 * k), (int)(760 * k));
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = DiaryPage.Paper;
            MinimumSize = new Size((int)(420 * k), (int)(400 * k));
            try { using (var bmp = Portrait.Make(c.Brain, 32, Face.Happy)) Icon = Icon.FromHandle(bmp.GetHicon()); } catch { }
            var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = DiaryPage.Paper };
            page = new DiaryPage(c, k) { Location = Point.Empty };
            scroller.Controls.Add(page);
            Controls.Add(scroller);
            scroller.Resize += (s, e) => page.Relayout(scroller.ClientSize.Width);
            refresh.Tick += (s, e) => page.Relayout(scroller.ClientSize.Width);
            Shown += (s, e) => { page.Relayout(scroller.ClientSize.Width); refresh.Start(); };
            FormClosed += (s, e) => refresh.Dispose();
        }
    }

    sealed class DiaryPage : Control
    {
        public static readonly Color Paper = Color.FromArgb(252, 248, 238);
        static readonly Color Ink = Color.FromArgb(52, 46, 60);
        static readonly Color Faint = Color.FromArgb(150, 140, 150);
        static readonly Color Rule = Color.FromArgb(222, 214, 200);

        readonly Creature c;
        readonly float k;
        readonly List<Item> items = new List<Item>();
        Bitmap portrait;

        class Item { public string Kind, Text; public float Y, H; }

        public DiaryPage(Creature c, float k)
        {
            this.c = c; this.k = k;
            DoubleBuffered = true;
            BackColor = Paper;
            try { portrait = Portrait.Make(c.Brain, (int)(96 * k), Face.Happy); } catch { }
        }

        Font HandBig { get { return Renderer.GetFont(Renderer.HandFont, 30 * k, FontStyle.Bold); } }
        Font Hand { get { return Renderer.GetFont(Renderer.HandFont, 17 * k, FontStyle.Bold); } }
        Font Body { get { return Renderer.GetFont("Segoe UI", 13.5f * k, FontStyle.Regular); } }
        Font Small { get { return Renderer.GetFont("Segoe UI", 12 * k, FontStyle.Italic); } }

        float Pad { get { return 26 * k; } }

        void Build()
        {
            items.Clear();
            var b = c.Brain;
            Add("title", b.Name + "'s diary");
            Add("sub", "hatched " + b.Born + " · " + b.Age() + " old · building superintendent · " + c.TraitWords() + ".");

            Add("h", "the world, as i understand it");
            Add("map", "");
            foreach (var m in c.World.Mons)
            {
                ScreenMemory sm;
                if (b.Screens.TryGetValue(m.Key, out sm))
                {
                    var edges = new List<string>();
                    foreach (var side in new[] { "L", "R" })
                    {
                        var e = b.Edge(m.Key + ":" + side);
                        if (e != null) edges.Add("found the " + (side == "L" ? "left" : "right") + " edge on " + e.Found + " (bonked " + e.Bonks + "×, booped " + e.Boops + "×)");
                    }
                    Add("li", sm.Nick + " (screen " + (m.Index + 1) + "), discovered " + sm.Found + ". " + (edges.Count > 0 ? string.Join("; ", edges) + "." : "haven't found its edges yet."));
                }
            }
            if (c.World.Mons.Any(m => !b.Screens.ContainsKey(m.Key))) Add("li", "i think there's more out there. i can feel it.");

            Add("h", "windows i've met");
            var apps = b.Apps.OrderByDescending(kv => kv.Value.StoodOn + kv.Value.Naps).ToList();
            if (apps.Count == 0) Add("p", "none yet. i haven't stood on anything but the floor.");
            foreach (var kv in apps.Take(14))
            {
                var a = kv.Value;
                int trust = a.StoodOn - 3 * a.Dropped - a.WalkedOff;
                string feel = a.Dropped >= 3 ? "i don't trust it." : trust > 8 ? "very reliable." : trust > 2 ? "pretty good." : "we're getting to know each other.";
                string stats = "stood on it " + a.StoodOn + "×" + (a.Naps > 0 ? ", napped " + a.Naps + "×" : "") + (a.Dropped > 0 ? ", it dropped me " + a.Dropped + "×" : "") + (a.WalkedOff > 0 ? ", walked off it " + a.WalkedOff + "×" : "");
                Add("li", a.Nick + " (" + kv.Key + ") — " + stats + ". " + (a.EdgesKnown ? "i know where it ends. " : "") + feel);
            }

            Add("h", "favorite spots");
            int favs = 0;
            foreach (var act in new[] { "nap", "dance", "sing", "sunbathe", "sniff", "peek", "climb", "boop" })
            {
                string p = b.FavoritePlace(act);
                if (p == null || b.Love(act, p) < 1.5) continue;
                favs++;
                Add("li", Creature.ActPhrase(act) + ": " + c.DescribePlace(p) + " (" + (int)b.Love(act, p) + "×)");
            }
            if (favs == 0) Add("p", "i don't have favorites yet. everything is new.");

            Add("h", "traditions");
            if (b.Rituals.Count == 0) Add("p", "no traditions yet. give me time. i'll start doing things for no reason and then i'll keep doing them.");
            foreach (var r in b.Rituals)
            {
                string steps = string.Join(", then ", r.Acts.Select((a, i) => Creature.ActPhrase(a) + c.PlaceSuffix(i < r.Places.Count ? r.Places[i] : null)));
                Add("li", r.Name + ": " + steps + ". usually in the " + r.When + ". done " + r.Done + "× on purpose (since " + r.Born + ").");
            }

            var habits = b.StrongHabits(5);
            if (habits.Count > 0)
            {
                Add("h", "habits i've noticed");
                foreach (var h in habits) Add("li", "after " + Creature.CtxPhrase(h[0]) + ", i usually do " + Creature.ActPhrase(h[1]) + ".");
            }

            if (b.Artists.Count > 0)
            {
                Add("h", "music in the building");
                if (c.NowPlaying != null) Add("p", "right now: \u201c" + c.NowPlaying.Title + "\u201d by " + c.NowPlaying.Artist + ". " + Music.Rating(c.NowPlaying) + " out of 7.");
                Add("p", "you play a lot of " + string.Join(", ", b.Artists.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key + " (" + (int)kv.Value + ")")) + ".");
            }

            Add("h", "you");
            Add("p", "i " + c.FeelingsAboutUser().Replace("adores", "adore").Replace("trusts", "trust").Replace("thinks", "think").Replace("is a little", "am a little").Replace("is suspicious", "am suspicious")
                + ". you've petted me " + b.Pets + "×, picked me up " + b.Pickups + "×, and thrown me " + b.Throws + "×." + (b.Welcomes > 0 ? " you've come back " + b.Welcomes + "×." : ""));

            if (b.Thoughts.Count > 0)
            {
                Add("h", "things i said recently");
                foreach (var t in Enumerable.Reverse(b.Thoughts).Take(12)) Add("li", t.At + " — " + t.Text);
            }
            Add("foot", "i write everything down in " + Home.Root + " — my journal is in there too.");
        }

        void Add(string kind, string text) { items.Add(new Item { Kind = kind, Text = text }); }

        public void Relayout(int width)
        {
            Build();
            Width = Math.Max(200, width);
            float y = Pad;
            float textW = Width - Pad * 2;
            using (var g = CreateGraphics())
            {
                foreach (var it in items)
                {
                    it.Y = y;
                    switch (it.Kind)
                    {
                        case "title":
                            float pw = portrait != null ? portrait.Width + 12 * k : 0;
                            it.H = Math.Max(portrait != null ? portrait.Height : 0, g.MeasureString(it.Text, HandBig, (int)(textW - pw)).Height);
                            break;
                        case "sub": it.H = g.MeasureString(it.Text, Small, (int)textW).Height + 6 * k; break;
                        case "h": it.Y += 14 * k; it.H = g.MeasureString(it.Text, Hand).Height + 4 * k; break;
                        case "map": it.H = MapHeight(textW) + 10 * k; break;
                        case "li": it.H = g.MeasureString(it.Text, Body, (int)(textW - 18 * k)).Height + 3 * k; break;
                        case "foot": it.Y += 16 * k; it.H = g.MeasureString(it.Text, Small, (int)textW).Height; break;
                        default: it.H = g.MeasureString(it.Text, Body, (int)textW).Height + 3 * k; break;
                    }
                    y = it.Y + it.H;
                }
            }
            Height = (int)(y + Pad);
            Invalidate();
        }

        float MapHeight(float w)
        {
            var mons = c.World.Mons;
            if (mons.Count == 0) return 0;
            float minX = mons.Min(m => m.Bounds.Left), maxX = mons.Max(m => m.Bounds.Right);
            float minY = mons.Min(m => m.Bounds.Top), maxY = mons.Max(m => m.Bounds.Bottom);
            return Math.Min(260 * k, w * (maxY - minY) / Math.Max(1, maxX - minX));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            float textW = Width - Pad * 2;
            using (var ink = new SolidBrush(Ink))
            using (var faint = new SolidBrush(Faint))
            using (var rule = new Pen(Rule, 1))
            {
                // notebook lines
                for (float ly = 34 * k; ly < Height; ly += 26 * k) g.DrawLine(rule, 0, ly, Width, ly);
                using (var margin = new Pen(Color.FromArgb(90, 230, 120, 120), 1.5f)) g.DrawLine(margin, Pad * 0.6f, 0, Pad * 0.6f, Height);

                foreach (var it in items)
                {
                    switch (it.Kind)
                    {
                        case "title":
                            float x = Pad;
                            if (portrait != null) { g.DrawImage(portrait, x, it.Y); x += portrait.Width + 12 * k; }
                            g.DrawString(it.Text, HandBig, ink, new RectangleF(x, it.Y + 18 * k, textW - (x - Pad), it.H));
                            break;
                        case "sub": g.DrawString(it.Text, Small, faint, new RectangleF(Pad, it.Y, textW, it.H)); break;
                        case "h":
                            using (var hb = new SolidBrush(Renderer.Hsl(c.Brain.Looks.Hue, 0.55, 0.38, 255)))
                                g.DrawString(it.Text, Hand, hb, Pad, it.Y);
                            break;
                        case "map": DrawMap(g, Pad, it.Y + 4 * k, textW, MapHeight(textW)); break;
                        case "li":
                            g.DrawString("•", Body, ink, Pad, it.Y);
                            g.DrawString(it.Text, Body, ink, new RectangleF(Pad + 18 * k, it.Y, textW - 18 * k, it.H));
                            break;
                        case "foot": g.DrawString(it.Text, Small, faint, new RectangleF(Pad, it.Y, textW, it.H)); break;
                        default: g.DrawString(it.Text, Body, ink, new RectangleF(Pad, it.Y, textW, it.H)); break;
                    }
                }
            }
        }

        /// <summary>A little hand-drawn-ish map: screens it knows, edges it has bonked, where it is, favorite spots.</summary>
        void DrawMap(Graphics g, float x, float y, float w, float h)
        {
            var mons = c.World.Mons;
            if (mons.Count == 0 || h <= 0) return;
            float minX = mons.Min(m => m.Bounds.Left), maxX = mons.Max(m => m.Bounds.Right);
            float minY = mons.Min(m => m.Bounds.Top), maxY = mons.Max(m => m.Bounds.Bottom);
            float sc = Math.Min(w / (maxX - minX), h / (maxY - minY));
            float ox = x + (w - (maxX - minX) * sc) / 2;
            Func<double, float> X = v => ox + (float)(v - minX) * sc;
            Func<double, float> Y = v => y + (float)(v - minY) * sc;
            var b = c.Brain;
            Color accent = Renderer.Hsl(b.Looks.Hue, b.Looks.Sat, b.Looks.Light, 255);

            foreach (var m in mons)
            {
                ScreenMemory sm;
                bool known = b.Screens.TryGetValue(m.Key, out sm);
                var r = new RectangleF(X(m.Bounds.Left) + 3, Y(m.Bounds.Top) + 3, (m.Bounds.Width) * sc - 6, (m.Bounds.Height) * sc - 6);
                using (var fill = new SolidBrush(known ? Color.FromArgb(255, 255, 253, 247) : Color.FromArgb(120, 235, 230, 220)))
                    g.FillRectangle(fill, r);
                using (var pen = new Pen(known ? Ink : Faint, 1.4f * k) { DashStyle = known ? DashStyle.Solid : DashStyle.Dash })
                    g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                using (var tb = new SolidBrush(known ? Ink : Faint))
                    g.DrawString(known ? sm.Nick : "???", Hand, tb, r.X + 8 * k, r.Y + 6 * k);

                // windows it knows, as it last saw them
                if (known)
                {
                    foreach (var win in c.World.Wins.Where(o => b.Apps.ContainsKey(o.App)).Reverse())
                    {
                        var wr = RectangleF.FromLTRB(Math.Max(r.Left, X(win.R.Left)), Math.Max(r.Top, Y(win.R.Top)), Math.Min(r.Right, X(win.R.Right)), Math.Min(r.Bottom, Y(win.R.Bottom)));
                        if (wr.Width < 4 || wr.Height < 4 || !r.Contains(wr.Left + 1, wr.Top + 1)) continue;
                        using (var wf = new SolidBrush(Color.FromArgb(40, accent))) g.FillRectangle(wf, wr);
                        using (var wp = new Pen(Color.FromArgb(140, accent), 1f)) g.DrawLine(wp, wr.Left, wr.Top, wr.Right, wr.Top);
                    }
                }
                // edges it has bonked into are drawn thick
                using (var edge = new Pen(accent, 4 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    if (b.Edge(m.Key + ":L") != null) g.DrawLine(edge, r.Left, r.Top + 4, r.Left, r.Bottom - 4);
                    if (b.Edge(m.Key + ":R") != null) g.DrawLine(edge, r.Right, r.Top + 4, r.Right, r.Bottom - 4);
                }
            }

            // favorite floor spots
            foreach (var pair in new[] { new[] { "nap", "z" }, new[] { "dance", "♪" }, new[] { "sing", "♫" } })
            {
                string p = b.FavoritePlace(pair[0]);
                Seg s; double px;
                if (p == null || !p.StartsWith("floor:") || !c.Resolve(p, out s, out px)) continue;
                using (var tb = new SolidBrush(accent))
                    g.DrawString(pair[1], Renderer.GetFont("Segoe UI Symbol", 15 * k, FontStyle.Bold), tb, X(px) - 6 * k, Y(s.Y) - 26 * k);
            }

            // and where it is right now
            float cx = X(c.X), cy = Y(c.Y);
            using (var dot = new SolidBrush(accent)) g.FillEllipse(dot, cx - 5 * k, cy - 10 * k, 10 * k, 9 * k);
            using (var pen = new Pen(Ink, 1.2f)) g.DrawEllipse(pen, cx - 5 * k, cy - 10 * k, 10 * k, 9 * k);
            using (var tb = new SolidBrush(Faint)) g.DrawString("me", Small, tb, cx + 6 * k, cy - 22 * k);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && portrait != null) portrait.Dispose();
            base.Dispose(disposing);
        }
    }
}
