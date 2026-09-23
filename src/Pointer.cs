using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>What the mouse did while they were talking: where it went, and where it settled (what "this" means).
    /// Physical screen pixels.</summary>
    internal sealed class PointerGesture
    {
        public List<Point> Path = new List<Point>();   // oldest first
        public Point Focus;
        public bool Moved;     // it went somewhere on purpose (pointed, circled, underlined), not just resting
        public bool Valid;     // false: nothing useful (no samples, or they were pointing at the creature itself)
    }

    /// <summary>The laser pointer: remembers the last 20 s of mouse movement so a spoken "what's this?" can be
    /// answered about the thing under the pointer, and works out what that thing is.</summary>
    internal static class Pointer
    {
        struct Sample { public long T; public int X, Y; }

        const int Hz = 30, KeepSeconds = 20;
        static readonly Sample[] ring = new Sample[Hz * KeepSeconds];
        static int head, count;
        static long lastSample;
        static readonly object gate = new object();
        static readonly Stopwatch clock = Stopwatch.StartNew();

        /// <summary>Set by the app: the creature's box on screen (pointing at it doesn't count).</summary>
        public static Func<Rectangle> Ignore;

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

        /// <summary>UI thread, every frame (it samples ~30 times a second).</summary>
        public static void Tick()
        {
            long ms = clock.ElapsedMilliseconds;
            if (ms - lastSample < 1000 / Hz) return;
            lastSample = ms;
            POINT p;
            if (!GetCursorPos(out p)) return;
            lock (gate)
            {
                ring[head] = new Sample { T = ms, X = p.X, Y = p.Y };
                head = (head + 1) % ring.Length;
                if (count < ring.Length) count++;
            }
        }

        /// <summary>Samples from the last `seconds`, oldest first.</summary>
        static List<Sample> Recent(double seconds)
        {
            long from = clock.ElapsedMilliseconds - (long)(seconds * 1000);
            var list = new List<Sample>();
            lock (gate)
            {
                for (int i = count; i >= 1; i--)
                {
                    Sample s = ring[(head - i + ring.Length) % ring.Length];
                    if (s.T >= from) list.Add(s);
                }
            }
            return list;
        }


        /// <summary>What the pointer did during the last `seconds` (roughly: while they were speaking). Any thread.</summary>
        public static PointerGesture Collect(double seconds)
        {
            var g = new PointerGesture();
            List<Sample> s = Recent(Math.Max(1.5, Math.Min(30, seconds)));
            if (s.Count < 2) return g;
            g.Path = s.Select(x => new Point(x.X, x.Y)).ToList();
            // it settles where it spent the most time (later counts a bit more: people point, then talk about it)
            const int r = 30;
            double best = -1;
            for (int i = 0; i < s.Count; i++)
            {
                int near = 0;
                for (int j = 0; j < s.Count; j++)
                {
                    int dx = s[i].X - s[j].X, dy = s[i].Y - s[j].Y;
                    if (dx * dx + dy * dy <= r * r) near++;
                }
                double w = near * (1 + 0.6 * i / s.Count);
                if (w > best) { best = w; g.Focus = new Point(s[i].X, s[i].Y); }
            }
            double travel = 0;
            for (int i = 1; i < s.Count; i++) travel += Math.Sqrt(Math.Pow(s[i].X - s[i - 1].X, 2) + Math.Pow(s[i].Y - s[i - 1].Y, 2));
            int w0 = s.Max(x => x.X) - s.Min(x => x.X), h0 = s.Max(x => x.Y) - s.Min(x => x.Y);
            g.Moved = travel > 80 && Math.Max(w0, h0) > 40;
            g.Valid = true;
            try
            {
                Func<Rectangle> ig = Ignore;
                if (ig != null && ig().Contains(g.Focus)) g.Valid = false;   // they were touching the creature, not pointing
            }
            catch { }
            return g;
        }

        // ================================================================== what's under it

        /// <summary>"button “Submit” in “Checkout - Google Chrome”. text there: “…”" via Windows accessibility, or null.
        /// Gives up after ~1 s (some apps are slow to answer). Never reads password fields. Any thread.</summary>
        public static string Describe(Point p)
        {
            string result = null;
            var t = new Thread(delegate() { try { result = DescribeNow(p); } catch (Exception ex) { DebugLog.Write("pointer: " + ex.Message); } });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(1200);
            return result;
        }

        static string DescribeNow(Point p)
        {
            var pt = new System.Windows.Point(p.X, p.Y);
            AutomationElement el = AutomationElement.FromPoint(pt);
            if (el == null) return null;
            if ((uint)el.Current.ProcessId == (uint)Process.GetCurrentProcess().Id) return null;
            var sb = new StringBuilder();
            string kind = (el.Current.LocalizedControlType ?? "").Trim();
            string name = Clip(el.Current.Name, 160);
            sb.Append(kind.Length > 0 ? kind : "something");
            if (name.Length > 0) sb.Append(" “").Append(name).Append("”");
            else
            {
                // a nameless piece of something bigger: say what it's part of
                AutomationElement up = TreeWalker.ControlViewWalker.GetParent(el);
                for (int i = 0; up != null && i < 4 && up != AutomationElement.RootElement; i++, up = TreeWalker.ControlViewWalker.GetParent(up))
                {
                    string n = Clip(up.Current.Name, 120);
                    if (n.Length == 0) continue;
                    sb.Append(" inside ").Append((up.Current.LocalizedControlType ?? "something").Trim()).Append(" “").Append(n).Append("”");
                    name = n;
                    break;
                }
            }
            bool said = name.Length > 0;
            if (!el.Current.IsPassword)
            {
                object vp;
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out vp))
                {
                    string v = Clip(((ValuePattern)vp).Current.Value, 160);
                    if (v.Length > 0 && v != name) { sb.Append(" with the value “").Append(v).Append("”"); said = true; }
                }
                string line = TextAt(el, pt);
                if (line.Length > 0 && line != name) { sb.Append(". the text right under the pointer: “").Append(line).Append("”"); said = true; }
            }
            string window = TopWindowName(el);
            if (window.Length > 0 && window != name) { sb.Append(", in the window “").Append(window).Append("”"); said = true; }
            return said ? sb.ToString() : null;   // "pane" on its own tells the model nothing
        }

        /// <summary>The line of text under the pointer, from the nearest element that exposes text (documents, pages).</summary>
        static string TextAt(AutomationElement el, System.Windows.Point pt)
        {
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement cur = el;
            for (int depth = 0; cur != null && depth < 8; depth++)
            {
                object tp;
                if (cur.TryGetCurrentPattern(TextPattern.Pattern, out tp))
                {
                    try
                    {
                        TextPatternRange range = ((TextPattern)tp).RangeFromPoint(pt);
                        foreach (TextUnit unit in new[] { TextUnit.Line, TextUnit.Paragraph, TextUnit.Word })
                        {
                            TextPatternRange r = range.Clone();
                            r.ExpandToEnclosingUnit(unit);
                            string text = Clip(r.GetText(400), 300);
                            if (text.Length > 1) return text;
                        }
                    }
                    catch { }
                    return "";
                }
                if (cur == AutomationElement.RootElement) break;
                cur = walker.GetParent(cur);
            }
            return "";
        }

        static string TopWindowName(AutomationElement el)
        {
            var walker = TreeWalker.ControlViewWalker;
            AutomationElement cur = el, last = el;
            for (int depth = 0; cur != null && depth < 40; depth++)
            {
                AutomationElement parent = walker.GetParent(cur);
                if (parent == null || parent == AutomationElement.RootElement) { last = cur; break; }
                cur = parent;
            }
            return Clip(last.Current.Name, 120);
        }

        static string Clip(string s, int max)
        {
            s = System.Text.RegularExpressions.Regex.Replace((s ?? "").Replace('￼', ' '), @"\s+", " ").Trim();   // (images show up as ￼)
            return s.Length > max ? s.Substring(0, max).TrimEnd() + "…" : s;
        }

        // ================================================================== drawing it on screenshots

        public static readonly Color Laser = Color.FromArgb(255, 45, 85);   // the red of the marks

        /// <summary>Four short corner ticks pointing in at a spot, leaving the spot itself clear (for the close-up).</summary>
        public static void MarkSpot(Graphics g, Point c, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float inner = 30 * scale, outer = 52 * scale;
            using (var halo = new Pen(Color.FromArgb(150, 255, 255, 255), 7 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var pen = new Pen(Color.FromArgb(240, Laser), 3.5f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                foreach (var d in new[] { new PointF(0.71f, 0.71f), new PointF(-0.71f, 0.71f), new PointF(0.71f, -0.71f), new PointF(-0.71f, -0.71f) })   // corners: clear of the line of text it points at
                {
                    float x1 = c.X + d.X * inner, y1 = c.Y + d.Y * inner, x2 = c.X + d.X * outer, y2 = c.Y + d.Y * outer;
                    g.DrawLine(halo, x1, y1, x2, y2);
                    g.DrawLine(pen, x1, y1, x2, y2);
                }
        }

        /// <summary>Draws the path and a ring around where it settled onto a capture of `bounds` (physical pixels).</summary>
        public static void Annotate(Graphics g, Rectangle bounds, PointerGesture gesture, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var pts = gesture.Path.Select(p => new PointF(p.X - bounds.Left, p.Y - bounds.Top)).ToList();
            if (gesture.Moved && pts.Count > 1)
                using (var pen = new Pen(Color.FromArgb(120, Laser), 3f * scale) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, pts.ToArray());
            float cx = gesture.Focus.X - bounds.Left, cy = gesture.Focus.Y - bounds.Top, rr = 24 * scale;
            using (var halo = new Pen(Color.FromArgb(120, 255, 255, 255), 7 * scale)) g.DrawEllipse(halo, cx - rr, cy - rr, rr * 2, rr * 2);
            using (var ring = new Pen(Color.FromArgb(235, Laser), 3.5f * scale)) g.DrawEllipse(ring, cx - rr, cy - rr, rr * 2, rr * 2);
        }
    }
}
