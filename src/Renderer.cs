using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace Bestie
{
    /// <summary>Draws the creature procedurally with GDI+ (no sprite assets). Units inside DrawCreature are
    /// "body units": origin at the feet, the body is ~46 wide and ~40 tall, +x is the way it faces.</summary>
    public static class Renderer
    {
        public static Color Hsl(double h, double s, double l, int a)
        {
            h = ((h % 360) + 360) % 360 / 360.0;
            s = Math.Max(0, Math.Min(1, s)); l = Math.Max(0, Math.Min(1, l));
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            Func<double, double> f = t =>
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1 / 6.0) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
                return p;
            };
            return Color.FromArgb(a, (int)(f(h + 1 / 3.0) * 255), (int)(f(h) * 255), (int)(f(h - 1 / 3.0) * 255));
        }

        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public static Font GetFont(string family, float px, FontStyle style)
        {
            string key = family + "|" + Math.Round(px * 2) + "|" + (int)style;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                try { f = new Font(family, (float)(Math.Round(px * 2) / 2), style, GraphicsUnit.Pixel); }
                catch { f = new Font("Segoe UI", px, style, GraphicsUnit.Pixel); }
                fonts[key] = f;
            }
            return f;
        }

        static string handFont;
        public static string HandFont
        {
            get
            {
                if (handFont != null) return handFont;
                handFont = "Segoe UI";
                using (var fc = new InstalledFontCollection())
                    foreach (var name in new[] { "Ink Free", "Segoe Print", "Comic Sans MS" })
                        foreach (var fam in fc.Families)
                            if (fam.Name == name) { handFont = name; return handFont; }
                return handFont;
            }
        }

        // =====================================================================

        public static void DrawCreature(Graphics g, Creature c, float ax, float ay)
        {
            var looks = c.Brain.Looks;
            double t = Clock.T;
            float s = (float)c.S;
            var saved = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(ax, ay);

            if (c.Pose == Pose.Egg) { DrawEgg(g, c, s, t); g.Restore(saved); return; }

            double rot = c.Rot + c.Tilt, sq = c.Squash, bob = 0;
            int facing = c.Facing;
            double armB = 25, armF = 25;     // arm angles: 0 = straight down, 90 = sideways, 180 = up
            bool moving = c.Moving || c.Pose == Pose.Walk;
            bool dangle = c.Pose == Pose.Held;

            switch (c.Pose)
            {
                case Pose.Walk:
                    bob = -Math.Abs(Math.Sin(c.WalkPhase)) * 2; rot += Math.Sin(c.WalkPhase) * 3; break;
                case Pose.Dance:
                    bob = -Math.Abs(Math.Sin(t * 7)) * 5; rot += Math.Sin(t * 3.5) * 12; sq += Math.Sin(t * 14) * 0.06;
                    armB = 140 + Math.Sin(t * 7) * 30; armF = 140 - Math.Sin(t * 7) * 30;
                    facing = Math.Sin(t * 1.7) > 0 ? 1 : -1; break;
                case Pose.Sing:
                    rot += Math.Sin(t * 2.2) * 8; armB = 70 + Math.Sin(t * 2.2) * 20; armF = 70 - Math.Sin(t * 2.2) * 20; break;
                case Pose.Sit: sq += 0.12; break;
                case Pose.Lie: sq += 0.26 + Math.Sin(t * 1.6) * 0.03; armB = armF = 60; break;
                case Pose.Sunbathe: sq += 0.22; armB = armF = 165; break;
                case Pose.Sniff: rot += 9 + Math.Sin(t * 11) * 2.5; break;
                case Pose.Peek: rot += 30; armF = 70; break;
                case Pose.Held: armB = 155 + Math.Sin(t * 9) * 15; armF = 155 - Math.Sin(t * 9) * 15; break;
                case Pose.Crouch: sq += 0.25; break;
                case Pose.Wave: armF = 125 + Math.Sin(t * 9) * 25; break;
                case Pose.Exercise: armB = armF = 90 + c.Squash * 120; break;
                case Pose.Spin: facing = Math.Sin(t * 16) > 0 ? 1 : -1; bob = -Math.Abs(Math.Sin(t * 8)) * 3; armB = armF = 100; break;
                case Pose.Climb: armB = 150 + Math.Sin(c.WalkPhase) * 25; armF = 150 - Math.Sin(c.WalkPhase) * 25; break;
            }
            if (c.Face == Face.Dizzy) rot += Math.Sin(t * 6) * 7;
            if (c.Vibing) { bob -= Math.Abs(Math.Sin(t * 6.3)) * 2.2; rot += Math.Sin(t * 3.15) * 5; armF = 60 + Math.Sin(t * 6.3) * 20; }   // bopping along to your music
            if (c.Mood == "listening") { sq -= 0.05; armF = 110; }
            sq += Math.Sin(t * 2.1) * 0.015;   // breathing
            sq = Math.Max(-0.5, Math.Min(0.55, sq));

            g.RotateTransform((float)rot);
            g.TranslateTransform(0, (float)(bob * s));
            float sy = (float)(1 - sq), sx = (float)(1 + sq * 0.8);

            // eyes follow a world-space direction; bring it into body space
            double r = -rot * Math.PI / 180;
            double lx = (c.LookX * Math.Cos(r) - c.LookY * Math.Sin(r)) * facing;
            double ly = c.LookX * Math.Sin(r) + c.LookY * Math.Cos(r);
            if (c.Pose == Pose.Peek) { lx = 0.5; ly = 0.9; }

            Color body = Hsl(looks.Hue, looks.Sat, looks.Light, 255);
            Color light = Hsl(looks.Hue, looks.Sat * 0.9, Math.Min(0.9, looks.Light + 0.1), 255);
            Color shade = Hsl(looks.Hue, looks.Sat, looks.Light - 0.12, 255);
            Color dark = Hsl(looks.Hue, looks.Sat * 0.8, looks.Light * 0.36, 255);
            Color feetC = Hsl(looks.Hue, looks.Sat, looks.Light * 0.75, 255);

            // feet don't squash, they just stay on the ground
            var feetState = g.Save();
            g.ScaleTransform(s * facing, s);
            if (c.Pose != Pose.Lie && c.Pose != Pose.Sunbathe)
                DrawFeet(g, c, t, moving, dangle, feetC, dark);
            g.Restore(feetState);

            g.TranslateTransform(0, -3 * s);   // body sits on its little feet
            g.ScaleTransform(sx * s * facing, sy * s);

            using (var pen = new Pen(dark, 2.1f) { LineJoin = LineJoin.Round })
            {
                DrawTopper(g, c, looks, t, pen, body, dark);

                using (var path = BodyPath(looks.Round, t))
                {
                    using (var brush = new LinearGradientBrush(new PointF(0, -41), new PointF(0, -1), light, shade))
                        g.FillPath(brush, path);

                    var clip = g.Clip;
                    g.SetClip(path, CombineMode.Intersect);
                    if (looks.Pattern == 1)
                        using (var b = new SolidBrush(Hsl(looks.Hue, looks.Sat * 0.5, Math.Min(0.94, looks.Light + 0.2), 190)))
                            g.FillEllipse(b, -12, -21, 28, 19);
                    else if (looks.Pattern == 2)
                        using (var b = new SolidBrush(Hsl(looks.Hue, looks.Sat, looks.Light - 0.2, 110)))
                        {
                            g.FillEllipse(b, 6, -38, 8, 7); g.FillEllipse(b, 15, -30, 5, 5); g.FillEllipse(b, -18, -34, 6, 6); g.FillEllipse(b, -21, -22, 4, 4);
                        }
                    using (var hl = new SolidBrush(Color.FromArgb(95, 255, 255, 255)))
                        g.FillEllipse(hl, -15, -37, 10, 6);
                    g.Clip = clip;

                    g.DrawPath(pen, path);
                }

                DrawArm(g, -21, -17, armB, -1, body, pen);
                DrawArm(g, 22, -17, armF, 1, body, pen);
                DrawFace(g, c, looks, t, lx, ly, dark);
            }
            g.Restore(saved);
        }

        static GraphicsPath BodyPath(double round, double t)
        {
            const int N = 64;
            var pts = new PointF[N];
            double nTop = 2.0 + round * 0.9, nBot = nTop + 1.0;
            for (int i = 0; i < N; i++)
            {
                double th = 2 * Math.PI * i / N, cs = Math.Cos(th), sn = Math.Sin(th);
                double n = sn > 0 ? nBot : nTop;
                double x = 23 * Math.Sign(cs) * Math.Pow(Math.Abs(cs), 2 / n);
                double y = 19 * Math.Sign(sn) * Math.Pow(Math.Abs(sn), 2 / n);
                double wob = 1 + 0.012 * Math.Sin(3 * th + t * 3);
                pts[i] = new PointF((float)(x * wob), (float)(-21 + y * wob));
            }
            var p = new GraphicsPath();
            p.AddPolygon(pts);
            return p;
        }

        static void DrawFeet(Graphics g, Creature c, double t, bool moving, bool dangle, Color fill, Color line)
        {
            double p = c.WalkPhase;
            float bx = -9, fx = 9, by = -2.6f, fy = -2.6f;
            if (moving)
            {
                bx += (float)(Math.Cos(p) * 2.5); by -= (float)(Math.Max(0, Math.Sin(p)) * 3.5);
                fx += (float)(Math.Cos(p + Math.PI) * 2.5); fy -= (float)(Math.Max(0, Math.Sin(p + Math.PI)) * 3.5);
            }
            if (dangle) { by += 1.5f + (float)Math.Sin(t * 9) * 1.2f; fy += 1.5f - (float)Math.Sin(t * 9) * 1.2f; }
            if (c.Pose == Pose.Sit) { bx = 4; fx = 13; by = fy = -2; }
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(line, 1.6f))
            {
                foreach (var pt in new[] { new PointF(bx, by), new PointF(fx, fy) })
                {
                    g.FillEllipse(b, pt.X - 6, pt.Y - 3.2f, 12, 6.4f);
                    g.DrawEllipse(pen, pt.X - 6, pt.Y - 3.2f, 12, 6.4f);
                }
            }
        }

        static void DrawArm(Graphics g, float x, float y, double angle, int side, Color fill, Pen pen)
        {
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.RotateTransform((float)(side > 0 ? -angle : angle));
            using (var b = new SolidBrush(fill))
            {
                g.FillEllipse(b, -3.2f, -1, 6.4f, 10);
                using (var p = new Pen(pen.Color, 1.6f)) g.DrawEllipse(p, -3.2f, -1, 6.4f, 10);
            }
            g.Restore(st);
        }

        static void DrawTopper(Graphics g, Creature c, Looks looks, double t, Pen pen, Color body, Color dark)
        {
            double a = c.Antenna * 0.6 + (c.Mood == "listening" ? Math.Sin(t * 10) * 0.08 : 0);
            float tipX = (float)(Math.Sin(a) * 10), tipY = (float)(-39 - Math.Cos(a) * 11);
            using (var thin = new Pen(dark, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                switch (looks.Topper)
                {
                    case 0: // sprout
                        g.DrawBezier(thin, 0, -37, 0, -42, tipX * 0.3f, tipY + 3, tipX, tipY);
                        using (var leaf = new SolidBrush(Hsl(105, 0.55, 0.5, 255)))
                        using (var lp = new Pen(Hsl(105, 0.6, 0.25, 255), 1.3f))
                        {
                            foreach (int dir in new[] { -1, 1 })
                            {
                                var st = g.Save();
                                g.TranslateTransform(tipX, tipY);
                                g.RotateTransform((float)(dir * 50 + a * 40));
                                g.FillEllipse(leaf, 0, -3, 10, 6);
                                g.DrawEllipse(lp, 0, -3, 10, 6);
                                g.Restore(st);
                            }
                        }
                        break;
                    case 1: // antenna with a bobble
                        g.DrawLine(thin, 0, -38, tipX, tipY - 1);
                        using (var ball = new SolidBrush(Hsl(looks.Hue + 150, 0.7, 0.62, 255)))
                            g.FillEllipse(ball, tipX - 3.5f, tipY - 4.5f, 7, 7);
                        using (var bp = new Pen(dark, 1.4f)) g.DrawEllipse(bp, tipX - 3.5f, tipY - 4.5f, 7, 7);
                        break;
                    case 2: // cowlick
                        using (var hair = new Pen(dark, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawBezier(hair, -2, -39, -1 + tipX * 0.2f, -50, 9 + tipX * 0.5f, -48, 4 + tipX * 0.4f, -43);
                        break;
                    case 3: // little horns
                        using (var horn = new SolidBrush(Hsl(40, 0.45, 0.88, 255)))
                        using (var hp = new Pen(dark, 1.4f) { LineJoin = LineJoin.Round })
                        {
                            foreach (int dir in new[] { -1, 1 })
                            {
                                var pts = new[] { new PointF(dir * 6, -37), new PointF(dir * 13 + tipX * 0.2f, -48), new PointF(dir * 14, -35) };
                                g.FillPolygon(horn, pts); g.DrawPolygon(hp, pts);
                            }
                        }
                        break;
                    default: // ears
                        using (var ear = new SolidBrush(body))
                        using (var inner = new SolidBrush(Hsl(345, 0.6, 0.8, 200)))
                        {
                            foreach (int dir in new[] { -1, 1 })
                            {
                                var st = g.Save();
                                g.TranslateTransform(dir * 11, -34);
                                g.RotateTransform((float)(dir * 22 + a * 25));
                                g.FillEllipse(ear, -5, -12, 10, 14);
                                g.FillEllipse(inner, -2.5f, -9, 5, 8);
                                g.DrawEllipse(pen, -5, -12, 10, 14);
                                g.Restore(st);
                            }
                        }
                        break;
                }
            }
        }

        static void DrawFace(Graphics g, Creature c, Looks looks, double t, double lx, double ly, Color dark)
        {
            float ey = -24, exB = -5, exF = 11;
            float mx = 3.5f, my = -14.5f;
            Face face = c.Face;
            if (c.Pose == Pose.Lie && face == Face.Normal) face = Face.Sleep;
            double blink = c.Blink;
            if (face == Face.Content) blink = Math.Max(blink, 0.45);
            if (c.Pose == Pose.Dance && face == Face.Normal) face = Face.Happy;

            using (var ink = new SolidBrush(Color.FromArgb(255, 34, 30, 40)))
            using (var line = new Pen(Color.FromArgb(255, 34, 30, 40), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // cheeks
                int blushA = (int)(45 + 150 * Math.Min(1, c.Blush));
                using (var cheek = new SolidBrush(Color.FromArgb(blushA, 255, 118, 140)))
                {
                    g.FillEllipse(cheek, exB - 7, -19, 7, 3.6f);
                    g.FillEllipse(cheek, exF + 1, -19, 7, 3.6f);
                }

                if (c.Sunglasses)
                {
                    using (var glass = new SolidBrush(Color.FromArgb(245, 25, 25, 30)))
                    {
                        FillRounded(g, glass, exB - 6.5f, ey - 4, 12, 8, 3);
                        FillRounded(g, glass, exF - 5.5f, ey - 4, 12, 8, 3);
                    }
                    g.DrawLine(line, exB + 5, ey - 2, exF - 5, ey - 2);
                    using (var shine = new Pen(Color.FromArgb(150, 255, 255, 255), 1.1f)) { g.DrawLine(shine, exB - 4, ey - 2, exB - 1, ey - 3); g.DrawLine(shine, exF - 3, ey - 2, exF, ey - 3); }
                }
                else
                {
                    switch (face)
                    {
                        case Face.Happy:
                            g.DrawArc(line, exB - 4, ey - 3, 8, 7, 200, 140);
                            g.DrawArc(line, exF - 4, ey - 3, 8, 7, 200, 140);
                            break;
                        case Face.Sleep:
                            g.DrawArc(line, exB - 4, ey - 5, 8, 6, 20, 140);
                            g.DrawArc(line, exF - 4, ey - 5, 8, 6, 20, 140);
                            break;
                        case Face.Dizzy:
                            Spiral(g, line, exB, ey, t); Spiral(g, line, exF, ey, t + 1);
                            break;
                        case Face.Squint:
                            g.DrawLines(line, new[] { new PointF(exB - 3, ey - 3), new PointF(exB + 2, ey), new PointF(exB - 3, ey + 3) });
                            g.DrawLines(line, new[] { new PointF(exF + 3, ey - 3), new PointF(exF - 2, ey), new PointF(exF + 3, ey + 3) });
                            break;
                        default:
                            bool surprised = face == Face.Surprised;
                            double jig = looks.Eyes == 3 ? c.AntennaV * 0.02 : 0;
                            switch (looks.Eyes)
                            {
                                case 1:
                                    Eye(g, exB, ey, 5.8f, 6.4f, true, 3.7f, lx, ly, blink, surprised, ink);
                                    Eye(g, exF, ey + 1, 3.8f, 4.3f, true, 2.5f, lx, ly, blink, surprised, ink);
                                    break;
                                case 2:
                                    Eye(g, exB, ey, 2.9f, 3.7f, false, 0, lx, ly, blink, surprised, ink);
                                    Eye(g, exF, ey, 2.9f, 3.7f, false, 0, lx, ly, blink, surprised, ink);
                                    break;
                                case 3:
                                    Eye(g, exB, ey, 5.4f, 5.4f, true, 2.5f, lx + jig, ly - jig, blink, surprised, ink);
                                    Eye(g, exF, ey, 5.4f, 5.4f, true, 2.5f, lx - jig, ly + jig, blink, surprised, ink);
                                    break;
                                default:
                                    Eye(g, exB, ey, 4.6f, 5.4f, true, 3.1f, lx, ly, blink, surprised, ink);
                                    Eye(g, exF, ey, 4.6f, 5.4f, true, 3.1f, lx, ly, blink, surprised, ink);
                                    break;
                            }
                            break;
                    }
                }

                // mouth
                bool open = c.Talk > 0.05 || c.Pose == Pose.Sing;
                if (open)
                {
                    float h = c.Pose == Pose.Sing ? (float)(2.5 + Math.Abs(Math.Sin(t * 5)) * 3) : (float)(1.5 + c.Talk * 3.2);
                    using (var m = new SolidBrush(Color.FromArgb(255, 92, 34, 48))) g.FillEllipse(m, mx - 2.4f, my - 1, 4.8f, h);
                }
                else if (face == Face.Happy || c.Pose == Pose.Sunbathe || face == Face.Content && c.Blush > 0.3)
                {
                    using (var gp = new GraphicsPath())
                    {
                        gp.AddArc(mx - 4, my - 4, 8, 8, 0, 180);
                        gp.CloseFigure();
                        using (var m = new SolidBrush(Color.FromArgb(255, 92, 34, 48))) g.FillPath(m, gp);
                        using (var tongue = new SolidBrush(Color.FromArgb(255, 240, 120, 130))) g.FillEllipse(tongue, mx - 2, my + 1.3f, 4, 2.6f);
                    }
                }
                else if (face == Face.Surprised) g.DrawEllipse(line, mx - 1.8f, my - 1.5f, 3.6f, 4.2f);
                else if (face == Face.Sleep) g.DrawEllipse(line, mx - 1.2f, my - 0.5f, 2.4f, 2.4f + (float)Math.Sin(t * 1.6) * 0.5f);
                else if (face == Face.Dizzy || face == Face.Squint)
                    g.DrawBezier(line, mx - 4, my, mx - 1.5f, my - 2.5f, mx + 1.5f, my + 2.5f, mx + 4, my);
                else if (face == Face.Think) g.DrawLine(line, mx - 1.5f, my + 0.5f, mx + 2, my);
                else g.DrawArc(line, mx - 3, my - 2.5f, 6, 4, 20, 140);
            }
        }

        static void Eye(Graphics g, float cx, float cy, float rw, float rh, bool white, float pupil,
                        double lx, double ly, double blink, bool surprised, Brush ink)
        {
            if (surprised) { rw *= 1.2f; rh *= 1.2f; pupil *= 0.6f; }
            if (blink > 0.75)
            {
                using (var p = new Pen(Color.FromArgb(255, 34, 30, 40), 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, cx - rw * 0.9f, cy + rh * 0.2f, cx + rw * 0.9f, cy + rh * 0.2f);
                return;
            }
            float h = rh * (float)(1 - blink * 0.9);
            float top = cy + rh - 2 * h;   // eyelids come down from the top
            double len = Math.Sqrt(lx * lx + ly * ly);
            if (len > 1) { lx /= len; ly /= len; }
            if (white)
            {
                g.FillEllipse(Brushes.White, cx - rw, top, rw * 2, h * 2);
                using (var p = new Pen(Color.FromArgb(255, 34, 30, 40), 1.1f)) g.DrawEllipse(p, cx - rw, top, rw * 2, h * 2);
                float px = cx + (float)(lx * (rw - pupil) * 0.85), py = top + h + (float)(ly * (h - pupil * 0.8) * 0.7);
                float ph = Math.Min(pupil, h);
                g.FillEllipse(ink, px - pupil, py - ph, pupil * 2, ph * 2);
                g.FillEllipse(Brushes.White, px - pupil * 0.55f, py - ph * 0.7f, pupil * 0.7f, pupil * 0.7f);
            }
            else
            {
                float px = cx + (float)(lx * 1.2), py = top + h + (float)(ly * 1.0);
                if (surprised)
                {
                    g.FillEllipse(Brushes.White, px - rw - 1.5f, py - h - 1.5f, rw * 2 + 3, h * 2 + 3);
                    using (var p = new Pen(Color.FromArgb(255, 34, 30, 40), 1f)) g.DrawEllipse(p, px - rw - 1.5f, py - h - 1.5f, rw * 2 + 3, h * 2 + 3);
                }
                g.FillEllipse(ink, px - rw, py - h, rw * 2, h * 2);
                g.FillEllipse(Brushes.White, px - rw * 0.5f, py - h * 0.65f, rw * 0.75f, rw * 0.75f);
            }
        }

        static void Spiral(Graphics g, Pen pen, float cx, float cy, double t)
        {
            var pts = new List<PointF>();
            for (int i = 0; i < 18; i++)
            {
                double a = i * 0.7 + t * 8, r = 0.4 + i * 0.28;
                pts.Add(new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r)));
            }
            using (var p = new Pen(pen.Color, 1.3f)) g.DrawLines(p, pts.ToArray());
        }

        static void FillRounded(Graphics g, Brush b, float x, float y, float w, float h, float r)
        {
            using (var p = Rounded(x, y, w, h, r)) g.FillPath(b, p);
        }

        public static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void DrawEgg(Graphics g, Creature c, float s, double t)
        {
            var looks = c.Brain.Looks;
            float wob = (float)(Math.Sin(t * 22) * 11 * Math.Min(1, c.EggWobble));
            g.RotateTransform(wob);
            g.ScaleTransform(s, s);
            using (var egg = new GraphicsPath())
            {
                egg.AddBezier(0, -48, 17, -48, 21, -18, 19, -10);
                egg.AddBezier(19, -10, 17, 1, -17, 1, -19, -10);
                egg.AddBezier(-19, -10, -21, -18, -17, -48, 0, -48);
                egg.CloseFigure();
                using (var b = new LinearGradientBrush(new PointF(0, -48), new PointF(0, 0), Hsl(45, 0.6, 0.95, 255), Hsl(38, 0.45, 0.8, 255)))
                    g.FillPath(b, egg);
                var clip = g.Clip;
                g.SetClip(egg, CombineMode.Intersect);
                using (var spot = new SolidBrush(Hsl(looks.Hue, looks.Sat, looks.Light, 200)))
                {
                    g.FillEllipse(spot, -11, -36, 7, 6); g.FillEllipse(spot, 6, -28, 9, 7); g.FillEllipse(spot, -14, -17, 6, 5);
                    g.FillEllipse(spot, 9, -12, 5, 5); g.FillEllipse(spot, 1, -44, 5, 4);
                }
                using (var hl = new SolidBrush(Color.FromArgb(120, 255, 255, 255))) g.FillEllipse(hl, -12, -42, 7, 12);
                g.Clip = clip;
                using (var pen = new Pen(Hsl(35, 0.3, 0.45, 255), 1.8f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, egg);
            }
            using (var crack = new Pen(Color.FromArgb(255, 70, 55, 45), 1.5f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // one zigzag crack that grows around the egg
                var zig = new[] { new PointF(-18, -27), new PointF(-12, -31), new PointF(-7, -26), new PointF(-2, -31), new PointF(3, -26), new PointF(8, -31), new PointF(13, -27), new PointF(18, -30) };
                int n = c.EggCrack <= 0 ? 0 : c.EggCrack == 1 ? 3 : c.EggCrack == 2 ? 5 : zig.Length;
                if (n >= 2) g.DrawLines(crack, zig.Take(n).ToArray());
            }
        }

        // =====================================================================
        // particles (screen coordinates, offset by the canvas origin)
        // =====================================================================

        public static void DrawParticles(Graphics g, Creature c, float ox, float oy)
        {
            float s = (float)c.S;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var looks = c.Brain.Looks;
            foreach (var p in c.Particles)
            {
                float x = (float)p.X - ox, y = (float)p.Y - oy;
                double life = p.Life / p.MaxLife;
                int a = (int)(255 * Math.Min(1, life * 2.2));
                switch (p.Kind)
                {
                    case "z":
                        using (var b = new SolidBrush(Color.FromArgb(a, 90, 100, 150)))
                            g.DrawString("z", GetFont(HandFont, (float)(11 + (1 - life) * 9) * s * (float)p.Size, FontStyle.Bold), b, x, y);
                        break;
                    case "heart":
                        using (var b = new SolidBrush(Color.FromArgb(a, 240, 80, 110))) Heart(g, b, x, y, 9 * s * (float)p.Size);
                        break;
                    case "note":
                        using (var b = new SolidBrush(Color.FromArgb(a, 60, 55, 90)))
                            g.DrawString(p.Rot > 0 ? "♪" : "♫", GetFont("Segoe UI Symbol", 15 * s, FontStyle.Bold), b, x, y + (float)Math.Sin(p.Life * 8) * 3 * s);
                        break;
                    case "star":
                        using (var b = new SolidBrush(Color.FromArgb(a, 255, 214, 60)))
                        using (var pen = new Pen(Color.FromArgb(a, 170, 120, 20), 1.2f))
                            Star(g, b, pen, x, y, 6 * s, p.Life * 6, 5);
                        break;
                    case "sparkle":
                        using (var b = new SolidBrush(Color.FromArgb(a, 255, 245, 190)))
                        using (var pen = new Pen(Color.FromArgb(a, 230, 180, 60), 1f))
                            Star(g, b, pen, x, y, (float)(5 + Math.Sin(life * Math.PI) * 4) * s, 0, 4);
                        break;
                    case "dust":
                        float r = (float)(p.Size * (1.5 - life));
                        using (var b = new SolidBrush(Color.FromArgb((int)(a * 0.5), 200, 195, 185))) g.FillEllipse(b, x - r, y - r, r * 2, r * 2);
                        break;
                    case "puff":
                        float pr = (float)(p.Size * (1.4 - life * 0.6));
                        using (var b = new SolidBrush(Color.FromArgb((int)(a * 0.6), 225, 225, 230))) g.FillEllipse(b, x - pr, y - pr, pr * 2, pr * 2);
                        break;
                    case "bang":
                    case "what":
                        using (var b = new SolidBrush(Color.FromArgb(a, 230, 70, 60)))
                            g.DrawString(p.Kind == "bang" ? "!" : "?", GetFont(HandFont, 22 * s, FontStyle.Bold), b, x - 6 * s, y - 14 * s);
                        break;
                    case "sweat":
                        using (var b = new SolidBrush(Color.FromArgb(a, 110, 180, 255))) g.FillEllipse(b, x - 2.5f * s, y - 3 * s, 5 * s, 6.5f * s);
                        break;
                    case "shell":
                        var st = g.Save();
                        g.TranslateTransform(x, y);
                        g.RotateTransform((float)((p.Rot + (1 - life) * 8) * 57.3));
                        using (var b = new SolidBrush(Color.FromArgb(a, 250, 240, 215)))
                        using (var pen = new Pen(Color.FromArgb(a, 150, 120, 90), 1.2f))
                        {
                            var pts = new[] { new PointF(-6 * s, 0), new PointF(-2 * s, -5 * s), new PointF(2 * s, -2 * s), new PointF(6 * s, -5 * s), new PointF(5 * s, 3 * s), new PointF(-5 * s, 3 * s) };
                            g.FillPolygon(b, pts); g.DrawPolygon(pen, pts);
                        }
                        g.Restore(st);
                        break;
                }
            }
        }

        static void Heart(Graphics g, Brush b, float x, float y, float size)
        {
            using (var p = new GraphicsPath())
            {
                p.AddBezier(x, y + size * 0.35f, x - size, y - size * 0.4f, x - size * 0.3f, y - size * 1.05f, x, y - size * 0.45f);
                p.AddBezier(x, y - size * 0.45f, x + size * 0.3f, y - size * 1.05f, x + size, y - size * 0.4f, x, y + size * 0.35f);
                g.FillPath(b, p);
            }
        }

        static void Star(Graphics g, Brush b, Pen pen, float x, float y, float r, double rot, int points)
        {
            var pts = new PointF[points * 2];
            for (int i = 0; i < pts.Length; i++)
            {
                double a = rot + Math.PI * i / points - Math.PI / 2;
                float rr = i % 2 == 0 ? r : r * (points == 4 ? 0.3f : 0.45f);
                pts[i] = new PointF(x + (float)Math.Cos(a) * rr, y + (float)Math.Sin(a) * rr);
            }
            g.FillPolygon(b, pts);
            g.DrawPolygon(pen, pts);
        }

        // =====================================================================
        // speech bubble
        // =====================================================================

        /// <summary>Measures the bubble for a line of text. Returns the size of the whole bubble including the tail.</summary>
        public static SizeF MeasureBubble(Graphics g, string text, float s, out SizeF textSize)
        {
            var font = GetFont(HandFont, 15 * s, FontStyle.Bold);
            textSize = g.MeasureString(text, font, (int)(230 * s));
            float pad = 11 * s;
            return new SizeF(textSize.Width + pad * 2, textSize.Height + pad * 1.4f + 12 * s);
        }

        /// <summary>Draws a bubble whose body occupies (x, y, w, h); the tail points at tailX, below (or above if tailUp).</summary>
        public static void DrawBubble(Graphics g, string text, bool thought, float x, float y, float w, float h, float tailX, bool tailUp, float s, int alpha)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fill = Color.FromArgb(alpha * 247 / 255, 255, 253, 248);
            var edge = Color.FromArgb(alpha, 70, 64, 76);
            using (var b = new SolidBrush(fill))
            using (var pen = new Pen(edge, 1.8f * s) { LineJoin = LineJoin.Round })
            {
                float r = Math.Min(14 * s, h / 2);
                if (thought)
                {
                    using (var p = Rounded(x, y, w, h, r)) { g.FillPath(b, p); g.DrawPath(pen, p); }
                    float dir = tailUp ? -1 : 1, by = tailUp ? y : y + h;
                    float c1 = 5 * s, c2 = 3.2f * s;
                    g.FillEllipse(b, tailX - c1, by + dir * 7 * s - c1, c1 * 2, c1 * 2); g.DrawEllipse(pen, tailX - c1, by + dir * 7 * s - c1, c1 * 2, c1 * 2);
                    g.FillEllipse(b, tailX + dir * 0 - c2 + 3 * s, by + dir * 15 * s - c2, c2 * 2, c2 * 2); g.DrawEllipse(pen, tailX - c2 + 3 * s, by + dir * 15 * s - c2, c2 * 2, c2 * 2);
                }
                else
                {
                    using (var p = new GraphicsPath())
                    {
                        float tw = 8 * s, th = 11 * s;
                        tailX = Math.Max(x + r + tw, Math.Min(x + w - r - tw, tailX));
                        if (!tailUp)
                        {
                            p.AddArc(x, y, r * 2, r * 2, 180, 90);
                            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                            p.AddLine(tailX + tw, y + h, tailX + 2 * s, y + h + th);
                            p.AddLine(tailX + 2 * s, y + h + th, tailX - tw, y + h);
                            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                        }
                        else
                        {
                            p.AddArc(x, y, r * 2, r * 2, 180, 90);
                            p.AddLine(tailX - tw, y, tailX + 2 * s, y - th);
                            p.AddLine(tailX + 2 * s, y - th, tailX + tw, y);
                            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
                            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
                            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
                        }
                        p.CloseFigure();
                        g.FillPath(b, p);
                        g.DrawPath(pen, p);
                    }
                }
            }
            var font = GetFont(HandFont, 15 * s, FontStyle.Bold);
            using (var tb = new SolidBrush(Color.FromArgb(alpha, 48, 42, 56)))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, tb, new RectangleF(x, y, w, h), fmt);
            }
        }
    }
}
