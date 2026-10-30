// VisualScene.cs - the drawing half of Bestie's on-screen visual explainers (Visuals.cs runs them). Claude writes a
// visual as JSON lines (one per thing), streamed: a frame, then steps, each with a narration line and the things
// drawn while that line is said. This turns those lines into a scene and draws it at any moment: strokes draw on,
// fills fade in, arrows follow what they connect, things move, spin and flow. Everything is laid out on a 1000 x 680
// board (title band, drawing area, caption), plus "screen" marks placed on the real screen around something the
// student is looking at. Pure drawing, no window (VisualWindow presents it), so it can be rendered to a PNG for
// checks. C# 5 only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace Bestie
{
    internal sealed class VisualElement
    {
        public string Kind, Id, Color, Label, Text, Fill, Align, LabelAt, Parent;
        public bool Curve, Dash, Shade, Shadow, Horizontal;
        public float X, Y, W, H, R, Rx, Ry, Size, Width, Bend, Rot, Depth;
        public List<PointF> Pts = new List<PointF>();
        public object From, To;                 // arrows: an id (string), a board point (PointF) or a screen point (ScreenPoint)
        public string[][] Values;
        public int Cols, Rows;
        public float Cell;
        public HashSet<string> Hi = new HashSet<string>();
        public int Step;
        // timing (scene seconds; -1 = not yet)
        public double Appear = -1, AppearDur = 0.9;
        public float OffX, OffY, MoveFromX, MoveFromY, MoveToX, MoveToY;
        public double MoveStart = -1, MoveDur = 1.2;
        public double SpinStart = -1, SpinSpeed = 1;
        public double FlowStart = -1, FlowSpeed = 1;
        public double PulseStart = -1, HideStart = -1;
        public float TurnFrom, TurnTo, PivotX = float.NaN, PivotY = float.NaN;   // "turn": to an angle (degrees) about a hinge
        public double TurnStart = -1, TurnDur = 1.2;
        public bool OnScreen { get { return Kind == "mark"; } }
    }

    internal struct ScreenPoint { public float X, Y; public ScreenPoint(float x, float y) { X = x; Y = y; } }

    /// <summary>A motion line ("move", "spin", "flow", "pulse", "hide") waiting for its moment.</summary>
    internal sealed class VisualMotion
    {
        public string Kind, Id;
        public Dictionary<string, object> D;
        public int Step;
        public double At = -1;
    }

    internal sealed class VisualStep
    {
        public string Say = "";
        public readonly List<object> Items = new List<object>();   // VisualElement or VisualMotion, in the order written
        public double Start = -1;
        public int Scheduled;          // how many of Items have been given a start time
        public double LastItemAt = -1;
    }

    internal sealed class VisualScene
    {
        public const float BoardW = 1000, BoardH = 680;
        public string Title = "";
        public string Place = "auto";
        public readonly List<VisualStep> Steps = new List<VisualStep>();
        public readonly List<VisualElement> Elements = new List<VisualElement>();
        readonly Dictionary<string, VisualElement> byId = new Dictionary<string, VisualElement>();
        public int ShotW = 1280, ShotH = 720;     // the screenshot the marks are placed on
        public int Current = -1;                  // the step being narrated
        public bool Ended;                        // the whole program has arrived
        public bool Done;                         // ...and every step has played
        public string Problem;                    // shown on the board instead (it couldn't be made)
        public string Footer = "";
        public double Shown = 0, Closing = -1;    // board fade in / out
        int autoId;

        // ------------------------------------------------------------------ reading lines

        /// <summary>One line Claude wrote. Returns false if it wasn't a line it could use.</summary>
        public bool AddLine(string line)
        {
            line = (line ?? "").Trim().TrimEnd(',');
            if (line.Length < 2 || line[0] != '{') return false;
            Dictionary<string, object> d = null;
            try { d = Json.Obj(line); } catch { }
            if (d == null)
            {
                // the slips seen in real runs: a stray ," before the closing brace, a trailing comma
                string fixedLine = System.Text.RegularExpressions.Regex.Replace(line, ",\\s*\"?\\s*}\\s*$", "}");
                fixedLine = System.Text.RegularExpressions.Regex.Replace(fixedLine, ",\\s*([}\\]])", "$1");
                try { d = Json.Obj(fixedLine); } catch { }
            }
            if (d == null) return false;
            string t = (Json.Str(d, "t") ?? Json.Str(d, "type") ?? "").ToLowerInvariant();
            if (t == "plan") return true;   // claude's own short plan, never drawn
            if (t == "frame")
            {
                Title = Json.Str(d, "title") ?? Title;
                string pl = (Json.Str(d, "place") ?? "auto").ToLowerInvariant();
                Place = pl == "left" || pl == "right" || pl == "center" ? pl : "auto";
                return true;
            }
            if (t == "step")
            {
                var s = new VisualStep();
                s.Say = (Json.Str(d, "say") ?? "").Trim();
                Steps.Add(s);
                return true;
            }
            if (Steps.Count == 0) Steps.Add(new VisualStep());
            VisualStep step = Steps[Steps.Count - 1];
            if (t == "rotate" || t == "tilt" || t == "swing") t = "turn";
            if (t == "move" || t == "spin" || t == "flow" || t == "pulse" || t == "hide" || t == "turn")
            {
                var m = new VisualMotion { Kind = t, Id = Json.Str(d, "id") ?? "", D = d, Step = Steps.Count - 1 };
                step.Items.Add(m);
                return true;
            }
            VisualElement e = Parse(t, d);
            if (e == null) return false;
            e.Step = Steps.Count - 1;
            Elements.Add(e);
            if (!string.IsNullOrEmpty(e.Id)) byId[e.Id] = e;
            step.Items.Add(e);
            return true;
        }

        VisualElement Parse(string t, Dictionary<string, object> d)
        {
            if (t == "rect" || t == "rectangle") t = "box";
            if (t == "polygon" || t == "shape") t = "poly";
            if (t == "path" || t == "curve") t = "line";
            if (t == "label") t = "text";
            if (t == "table" || t == "matrix") t = "grid";
            if (t == "highlight" || t == "ring") t = "mark";
            if (t == "cube" || t == "block" || t == "cuboid" || t == "box3") t = "box3d";
            if (t == "can" || t == "cyl") t = "cylinder";
            bool sphere = t == "sphere" || t == "ball";
            if (sphere) t = "circle";
            if (t != "box" && t != "circle" && t != "ellipse" && t != "poly" && t != "line" && t != "arrow" && t != "text" && t != "grid" && t != "mark"
                && t != "box3d" && t != "cylinder") return null;
            var e = new VisualElement();
            e.Kind = t;
            e.Id = Json.Str(d, "id") ?? ("_" + (++autoId));
            e.Color = (Json.Str(d, "color") ?? (t == "text" ? "ink" : t == "mark" ? "orange" : "blue")).Trim().ToLowerInvariant();
            e.Label = Json.Str(d, "label");
            e.LabelAt = (Json.Str(d, "labelAt") ?? Json.Str(d, "label_at") ?? "").ToLowerInvariant();
            e.Text = Json.Str(d, "text") ?? e.Label ?? "";
            e.Fill = (Json.Str(d, "fill") ?? (t == "line" || t == "arrow" || t == "text" || t == "mark" ? "none" : "soft")).ToLowerInvariant();
            e.Align = (Json.Str(d, "align") ?? "center").ToLowerInvariant();
            e.X = F(d, "x", 500); e.Y = F(d, "y", 340);
            e.W = F(d, "w", 160); e.H = F(d, "h", 90);
            e.R = F(d, "r", t == "box" ? 14 : 50);
            e.Rx = F(d, "rx", 80); e.Ry = F(d, "ry", 50);
            e.Size = F(d, "size", t == "text" ? 26 : 20);
            e.Width = F(d, "width", t == "arrow" ? 4.5f : t == "line" ? 4f : 3.2f);
            e.Bend = F(d, "bend", 0);
            if (sphere && !d.ContainsKey("fill")) e.Fill = "solid";
            if (t == "box3d" || t == "cylinder") { if (!d.ContainsKey("fill")) e.Fill = "solid"; if (!d.ContainsKey("width")) e.Width = 2.2f; }
            e.Parent = Json.Str(d, "in") ?? Json.Str(d, "parent");
            e.Rot = F(d, "rot", 0);
            e.Depth = F(d, "d", F(d, "depth", -1));
            e.Horizontal = (Json.Str(d, "axis") ?? "").StartsWith("h", StringComparison.OrdinalIgnoreCase);
            e.Shade = Json.Bool(d, "shade", sphere || e.Fill == "solid");
            e.Shadow = Json.Bool(d, "shadow", e.Fill == "solid" && e.Parent == null && t != "line" && t != "arrow");
            e.Curve = Json.Bool(d, "curve", false) || Json.Bool(d, "smooth", false);
            e.Dash = Json.Bool(d, "dash", false) || Json.Bool(d, "dashed", false);
            foreach (object o in Json.Arr(d, "pts").Length > 0 ? Json.Arr(d, "pts") : Json.Arr(d, "points"))
            {
                PointF p;
                if (Pt(o, out p)) e.Pts.Add(p);
            }
            if (t == "arrow")
            {
                e.From = End(d, "from");
                e.To = End(d, "to");
                if (e.From == null && e.Pts.Count >= 2) e.From = e.Pts[0];
                if (e.To == null && e.Pts.Count >= 2) e.To = e.Pts[e.Pts.Count - 1];
                if (e.From == null || e.To == null) return null;
            }
            if ((t == "line" || t == "poly") && e.Pts.Count < 2) return null;
            if (t == "grid")
            {
                object[] rows = Json.Arr(d, "values");
                e.Rows = (int)Json.Num(d, "rows", rows.Length > 0 ? rows.Length : 3);
                e.Cols = (int)Json.Num(d, "cols", 3);
                var vals = new List<string[]>();
                foreach (object r in rows)
                {
                    var cells = new List<string>();
                    var ra = r as object[];
                    var rl = r as System.Collections.ArrayList;
                    IEnumerable<object> items = ra != null ? (IEnumerable<object>)ra : rl != null ? rl.Cast<object>() : new object[] { r };
                    foreach (object c in items) cells.Add(c == null ? "" : Convert.ToString(c, CultureInfo.InvariantCulture));
                    vals.Add(cells.ToArray());
                    if (cells.Count > e.Cols && !d.ContainsKey("cols")) e.Cols = cells.Count;
                }
                e.Values = vals.ToArray();
                e.Rows = Math.Max(1, Math.Min(12, e.Rows));
                e.Cols = Math.Max(1, Math.Min(16, e.Cols));
                e.Cell = F(d, "cell", 56);
                foreach (object h in Json.Arr(d, "hi"))
                {
                    PointF p;
                    if (Pt(h, out p)) e.Hi.Add((int)p.X + "," + (int)p.Y);   // [row, col]
                }
            }
            if (t == "mark")
            {
                if (!d.ContainsKey("w")) e.W = 120;
                if (!d.ContainsKey("h")) e.H = 80;
            }
            return e;
        }

        static float F(Dictionary<string, object> d, string k, float fallback)
        {
            double v = Json.Num(d, k, double.NaN);
            return double.IsNaN(v) || double.IsInfinity(v) ? fallback : (float)v;
        }

        static bool Pt(object o, out PointF p)
        {
            p = PointF.Empty;
            var a = o as object[];
            if (a == null) { var l = o as System.Collections.ArrayList; if (l != null) a = l.ToArray(); }
            if (a == null || a.Length < 2) return false;
            try
            {
                p = new PointF(Convert.ToSingle(a[0], CultureInfo.InvariantCulture), Convert.ToSingle(a[1], CultureInfo.InvariantCulture));
                return !float.IsNaN(p.X) && !float.IsNaN(p.Y);
            }
            catch { return false; }
        }

        static object End(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            var s = v as string;
            if (s != null) return s.Trim();
            PointF p;
            if (Pt(v, out p)) return p;
            var o = v as Dictionary<string, object>;
            if (o != null)
            {
                object sp;
                if (o.TryGetValue("screen", out sp) && Pt(sp, out p)) return new ScreenPoint(p.X, p.Y);
                string id = Json.Str(o, "id");
                if (!string.IsNullOrEmpty(id)) return id;
            }
            return null;
        }

        public VisualElement Find(string id)
        {
            VisualElement e;
            return id != null && byId.TryGetValue(id, out e) ? e : null;
        }

        // ------------------------------------------------------------------ timing

        /// <summary>Gives the current step's newly arrived items their start times (staggered so they draw one after another).</summary>
        public void Schedule(double now)
        {
            if (Current < 0 || Current >= Steps.Count) return;
            VisualStep s = Steps[Current];
            while (s.Scheduled < s.Items.Count)
            {
                object item = s.Items[s.Scheduled++];
                double at = s.LastItemAt < 0 ? now + 0.15 : Math.Max(now, s.LastItemAt + Stagger(s));
                s.LastItemAt = at;
                var e = item as VisualElement;
                if (e != null) { Settle(e); e.Appear = at; e.AppearDur = DrawTime(e); continue; }
                var m = (VisualMotion)item;
                m.At = at;
                StartMotion(m, at);
            }
        }

        // a step that builds a detailed object (many parts) draws them quickly, so it still fits what's being said
        /// <summary>Things that can be slid out of each other's way (shapes standing on their own, words, tables).</summary>
        static bool Movable(VisualElement e)
        {
            return e.Parent == null && !e.OnScreen && e.Kind != "arrow" && e.Kind != "line";
        }

        static float Overlap(RectangleF a, RectangleF b)
        {
            RectangleF i = RectangleF.Intersect(a, b);
            return i.IsEmpty ? 0 : i.Width * i.Height;
        }

        /// <summary>A new shape that lands on top of one already showing (not a part sitting on its whole, which is on
        /// purpose) slides to the nearest free spot on the board before it's drawn, its parts with it. Claude's layouts
        /// overlap now and then (Haiku more often); this keeps the board readable without asking it again.</summary>
        void Settle(VisualElement e)
        {
            if (!Movable(e)) return;
            RectangleF mine = Bounds(e);
            if (mine.Width <= 0 || mine.Height <= 0) return;
            var others = new List<RectangleF>();
            foreach (VisualElement o in Elements)
            {
                if (o == e || o.Appear < 0 || o.HideStart >= 0 || !Movable(o)) continue;
                RectangleF ob = Bounds(o);
                ob.Offset(o.OffX, o.OffY);
                // a part on its whole, or a whole drawn round its parts: meant to sit together
                PointF mc = new PointF(mine.X + mine.Width / 2, mine.Y + mine.Height / 2), oc = new PointF(ob.X + ob.Width / 2, ob.Y + ob.Height / 2);
                if (ob.Contains(mc) && ob.Width * ob.Height > mine.Width * mine.Height * 1.6f) continue;
                if (mine.Contains(oc) && mine.Width * mine.Height > ob.Width * ob.Height * 1.6f) continue;
                others.Add(RectangleF.Inflate(ob, 8, 8));
            }
            Func<float, float, bool> clear = (dx, dy) =>
            {
                RectangleF m = mine; m.Offset(dx, dy);
                foreach (RectangleF r in others) if (Overlap(m, r) > Math.Min(m.Width * m.Height, r.Width * r.Height) * 0.12f) return false;
                return true;
            };
            if (clear(0, 0)) return;   // only a real overlap moves anything
            // a new spot must stay on the board (where it is now may already poke out; that's Claude's call)
            Func<float, float, bool> onBoard = (dx, dy) =>
            {
                RectangleF m = mine; m.Offset(dx, dy);
                return m.Left >= Math.Min(25, mine.Left) && m.Right <= Math.Max(BoardW - 25, mine.Right) && m.Top >= Math.Min(75, mine.Top) && m.Bottom <= Math.Max(590, mine.Bottom);
            };
            // nearest free spot: down first (reading order), then sideways, then up, a little further each ring
            for (int ring = 1; ring <= 16; ring++)
            {
                float s = ring * 22;
                float[][] tries = { new[] { 0f, s }, new[] { s, 0f }, new[] { -s, 0f }, new[] { 0f, -s }, new[] { s, s }, new[] { -s, s }, new[] { s, -s }, new[] { -s, -s } };
                foreach (float[] t in tries)
                    if (onBoard(t[0], t[1]) && clear(t[0], t[1])) { e.OffX += t[0]; e.OffY += t[1]; return; }
            }
        }

        static double Stagger(VisualStep s) { return s.Items.Count > 10 ? 0.16 : s.Items.Count > 5 ? 0.26 : 0.42; }

        double DrawTime(VisualElement e)
        {
            switch (e.Kind)
            {
                case "text": return 0.5;
                case "grid": return Math.Min(1.8, 0.35 + 0.06 * e.Rows * e.Cols);
                case "mark": return 0.8;
                case "line":
                case "arrow":
                    {
                        float len = 0;
                        List<PointF> pts = e.Kind == "arrow" ? new List<PointF> { Center(EndOf(e.From), 0), Center(EndOf(e.To), 0) } : e.Pts;
                        for (int i = 1; i < pts.Count; i++) len += Dist(pts[i - 1], pts[i]);
                        return Math.Max(0.45, Math.Min(1.3, 0.35 + len / 1100.0));
                    }
                default: return 0.9;
            }
        }

        void StartMotion(VisualMotion m, double at)
        {
            VisualElement e = Find(m.Id);
            if (e == null) return;
            if (m.Kind == "move")
            {
                PointF to, by;
                e.MoveFromX = e.OffX; e.MoveFromY = e.OffY;
                object v;
                if (m.D.TryGetValue("by", out v) && Pt(v, out by)) { e.MoveToX = e.OffX + by.X; e.MoveToY = e.OffY + by.Y; }
                else if (m.D.TryGetValue("to", out v) && Pt(v, out to))
                {
                    PointF c = BaseCenter(e);
                    e.MoveToX = to.X - c.X; e.MoveToY = to.Y - c.Y;
                }
                else return;
                e.MoveStart = at;
                e.MoveDur = Math.Max(0.3, Math.Min(6, Json.Num(m.D, "dur", 1.4)));
            }
            else if (m.Kind == "turn")
            {
                float now0 = TurnNow(e, at);
                e.TurnFrom = now0;
                e.TurnTo = m.D.ContainsKey("to") ? F(m.D, "to", 0) : now0 + F(m.D, "by", 30);
                PointF pv;
                object pivot;
                if (m.D.TryGetValue("pivot", out pivot) && Pt(pivot, out pv)) { e.PivotX = pv.X; e.PivotY = pv.Y; }
                e.TurnStart = at;
                e.TurnDur = Math.Max(0.3, Math.Min(6, Json.Num(m.D, "dur", 1.2)));
            }
            else if (m.Kind == "spin") { e.SpinStart = at; e.SpinSpeed = Math.Max(-4, Math.Min(4, Json.Num(m.D, "speed", 1))); }
            else if (m.Kind == "flow") { e.FlowStart = at; e.FlowSpeed = Math.Max(0.2, Math.Min(4, Json.Num(m.D, "speed", 1))); }
            else if (m.Kind == "pulse") e.PulseStart = at;
            else if (m.Kind == "hide") e.HideStart = at;
        }

        /// <summary>True once every item of the current step has been drawn and every move finished.</summary>
        public bool StepDrawn(double now)
        {
            if (Current < 0 || Current >= Steps.Count) return true;
            VisualStep s = Steps[Current];
            if (s.Scheduled < s.Items.Count) return false;
            foreach (object item in s.Items)
            {
                var e = item as VisualElement;
                if (e != null) { if (e.Appear < 0 || now < e.Appear + e.AppearDur) return false; continue; }
                var m = (VisualMotion)item;
                VisualElement target = Find(m.Id);
                if (m.Kind == "move" && target != null && now < target.MoveStart + target.MoveDur) return false;
                if (m.Kind == "turn" && target != null && now < target.TurnStart + target.TurnDur) return false;
            }
            return true;
        }

        /// <summary>Anything still moving (so the window keeps redrawing).</summary>
        public bool Animating(double now)
        {
            if (now < Shown + 0.4 || Closing >= 0) return true;
            if (!Ended || !Done) return true;
            foreach (VisualElement e in Elements)
            {
                if (e.SpinStart >= 0 || e.FlowStart >= 0) return true;
                if (e.Appear >= 0 && now < e.Appear + e.AppearDur + 0.1) return true;
                if (e.MoveStart >= 0 && now < e.MoveStart + e.MoveDur + 0.1) return true;
                if (e.TurnStart >= 0 && now < e.TurnStart + e.TurnDur + 0.1) return true;
                if (e.PulseStart >= 0 && now < e.PulseStart + 1.0) return true;
                if (e.HideStart >= 0 && now < e.HideStart + 0.5) return true;
                if (e.Kind == "mark" && e.Appear >= 0 && now < e.Appear + 4) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ where the board goes

        /// <summary>The board's place on a monitor, in surface pixels (the surface covers `bounds`): beside whatever the
        /// marks point at on the real screen, else where the model asked, else away from the mouse.</summary>
        public RectangleF PlaceBoard(Rectangle bounds, Rectangle work, int mouseX)
        {
            var wk = new RectangleF(work.X - bounds.X, work.Y - bounds.Y, work.Width, work.Height);
            float margin = 28;
            float want = Place == "center" ? wk.Width * 0.62f : wk.Width * 0.44f;
            want = Math.Max(Math.Min(560, wk.Width - 40), Math.Min(want, 1040));
            string side = Place;
            if (side == "auto") side = mouseX - bounds.X > wk.X + wk.Width / 2 ? "left" : "right";
            float w = want;

            // keep clear of what the marks point at
            RectangleF marks = RectangleF.Empty;
            screen = new RectangleF(0, 0, bounds.Width, bounds.Height);
            foreach (VisualElement e in Elements)
            {
                if (!e.OnScreen) continue;
                PointF a = ShotToPx(e.X, e.Y), b = ShotToPx(e.X + e.W, e.Y + e.H);
                var r = RectangleF.FromLTRB(a.X - 20, a.Y - 40, b.X + 20, b.Y + 20);
                marks = marks.IsEmpty ? r : RectangleF.Union(marks, r);
            }
            if (!marks.IsEmpty)
            {
                // the biggest board that fits beside, above or below what the marks point at
                float aspect = BoardH / BoardW;
                var room = new Dictionary<string, RectangleF>
                {
                    { "left", RectangleF.FromLTRB(wk.X + margin, wk.Y + margin, marks.Left - margin, wk.Bottom - margin) },
                    { "right", RectangleF.FromLTRB(marks.Right + margin, wk.Y + margin, wk.Right - margin, wk.Bottom - margin) },
                    { "above", RectangleF.FromLTRB(wk.X + margin, wk.Y + margin, wk.Right - margin, marks.Top - margin) },
                    { "below", RectangleF.FromLTRB(wk.X + margin, marks.Bottom + margin, wk.Right - margin, wk.Bottom - margin) },
                };
                string best = null; float bestW = 0;
                foreach (KeyValuePair<string, RectangleF> r in room)
                {
                    if (r.Value.Width <= 0 || r.Value.Height <= 0) continue;
                    float fit = Math.Min(want, Math.Min(r.Value.Width, r.Value.Height / aspect));
                    if (fit > bestW) { bestW = fit; best = r.Key; }
                }
                if (best != null && bestW >= Math.Min(600, want))
                {
                    RectangleF r = room[best];
                    float bh = bestW * aspect;
                    float bx = best == "left" ? r.Right - bestW : best == "right" ? r.X : marks.X + marks.Width / 2 - bestW / 2;
                    float by = best == "above" ? r.Bottom - bh : best == "below" ? r.Y : r.Y + (r.Height - bh) * 0.42f;
                    bx = Math.Max(wk.X + margin, Math.Min(bx, wk.Right - margin - bestW));
                    by = Math.Max(wk.Y + margin, Math.Min(by, wk.Bottom - margin - bh));
                    return new RectangleF(bx, by, bestW, bh);
                }
                // nowhere roomy: full size on the side it covers least (the marks still draw on top)
                side = marks.Left - wk.X >= wk.Right - marks.Right ? "left" : "right";
            }
            float h = w * BoardH / BoardW;
            if (h > wk.Height - 60) { h = wk.Height - 60; w = h * BoardW / BoardH; }
            float x = side == "left" ? wk.X + margin : side == "right" ? wk.Right - w - margin : wk.X + (wk.Width - w) / 2;
            float y = wk.Y + Math.Max(24, (wk.Height - h) * 0.42f);
            return new RectangleF(x, y, w, h);
        }

        // ------------------------------------------------------------------ geometry (board units)

        static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }

        static double Ease(double x) { x = Clamp01(x); return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2; }
        static double EaseOut(double x) { x = Clamp01(x); return 1 - Math.Pow(1 - x, 3); }
        static double Clamp01(double x) { return x < 0 ? 0 : x > 1 ? 1 : x; }

        PointF Offset(VisualElement e, double now)
        {
            if (e.MoveStart < 0) return new PointF(e.OffX, e.OffY);
            double k = Ease((now - e.MoveStart) / e.MoveDur);
            if (now >= e.MoveStart + e.MoveDur) { e.OffX = e.MoveToX; e.OffY = e.MoveToY; }
            return new PointF((float)(e.MoveFromX + (e.MoveToX - e.MoveFromX) * k), (float)(e.MoveFromY + (e.MoveToY - e.MoveFromY) * k));
        }

        /// <summary>A point of e (board units) where it is now: its own move, its own spin about its centre, then the
        /// same for the whole it sits in ("in"), so moving or spinning a jet carries its parts along.</summary>
        PointF PlaceAt(VisualElement e, PointF p, double now, int depth)
        {
            PointF o = Offset(e, now);
            float turn = TurnNow(e, now);
            if (turn != 0)
            {
                PointF pv = float.IsNaN(e.PivotX) ? BaseCenter(e) : new PointF(e.PivotX, e.PivotY);
                p = Rotate(p, pv, turn * Math.PI / 180);
            }
            p = new PointF(p.X + o.X, p.Y + o.Y);
            if (e.SpinStart >= 0 && now > e.SpinStart)
            {
                PointF bc = BaseCenter(e);
                double ang = (now - e.SpinStart) * e.SpinSpeed * Math.PI * Clamp01((now - e.SpinStart) / 0.8);   // speed 1 = half a turn a second
                p = Rotate(p, new PointF(bc.X + o.X, bc.Y + o.Y), ang);
            }
            VisualElement parent = depth < 6 && e.Parent != null && e.Parent != e.Id ? Find(e.Parent) : null;
            return parent != null ? PlaceAt(parent, p, now, depth + 1) : p;
        }

        static float TurnNow(VisualElement e, double now)
        {
            if (e.TurnStart < 0) return 0;
            if (now <= e.TurnStart) return e.TurnFrom;
            return (float)(e.TurnFrom + (e.TurnTo - e.TurnFrom) * Ease((now - e.TurnStart) / e.TurnDur));
        }

        List<PointF> PlaceAll(VisualElement e, List<PointF> pts, double now)
        {
            var outp = new List<PointF>(pts.Count);
            foreach (PointF p in pts) outp.Add(PlaceAt(e, p, now, 0));
            return outp;
        }

        static PointF BaseCenter(VisualElement e)
        {
            switch (e.Kind)
            {
                case "box3d": return new PointF(e.X + e.W / 2, e.Y + e.H / 2);
                case "box": case "grid": case "mark": case "cylinder":
                    {
                        float w = e.Kind == "grid" ? e.Cols * e.Cell : e.W, h = e.Kind == "grid" ? e.Rows * e.Cell : e.H;
                        return new PointF(e.X + w / 2, e.Y + h / 2);
                    }
                case "poly": case "line":
                    {
                        if (e.Pts.Count == 0) return new PointF(e.X, e.Y);
                        float minX = e.Pts.Min(p => p.X), maxX = e.Pts.Max(p => p.X), minY = e.Pts.Min(p => p.Y), maxY = e.Pts.Max(p => p.Y);
                        return new PointF((minX + maxX) / 2, (minY + maxY) / 2);
                    }
                default: return new PointF(e.X, e.Y);   // circle, ellipse, text: x,y is the centre
            }
        }

        PointF Center(object end, double now)
        {
            if (end is PointF) return (PointF)end;
            if (end is ScreenPoint) return ShotToBoard((ScreenPoint)end);
            VisualElement e = Find(end as string);
            if (e == null) return new PointF(BoardW / 2, BoardH / 2);
            if (!e.OnScreen) return PlaceAt(e, BaseCenter(e), now, 0);
            PointF c = ShotToBoard(new ScreenPoint(e.X + e.W / 2, e.Y + e.H / 2));
            PointF o = Offset(e, now);
            return new PointF(c.X + o.X, c.Y + o.Y);
        }

        object EndOf(object end) { return end; }

        /// <summary>Where a ray from the centre of e toward `toward` leaves e's outline (plus a small gap).</summary>
        PointF Edge(object end, PointF toward, double now)
        {
            PointF c = Center(end, now);
            VisualElement e = Find(end as string);
            if (e == null || e.OnScreen) return c;
            float dx = toward.X - c.X, dy = toward.Y - c.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return c;
            dx /= len; dy /= len;
            float gap = 7, reach;
            switch (e.Kind)
            {
                case "circle": reach = e.R; break;
                case "ellipse": reach = (float)(e.Rx * e.Ry / Math.Sqrt(e.Ry * e.Ry * dx * dx + e.Rx * e.Rx * dy * dy)); break;
                case "text": reach = 0; gap = 14; break;
                default:
                    {
                        RectangleF b = Bounds(e);
                        float tx = Math.Abs(dx) < 1e-4 ? float.MaxValue : b.Width / 2 / Math.Abs(dx);
                        float ty = Math.Abs(dy) < 1e-4 ? float.MaxValue : b.Height / 2 / Math.Abs(dy);
                        reach = Math.Min(tx, ty);
                        break;
                    }
            }
            reach = Math.Min(reach + gap, len * 0.45f);
            return new PointF(c.X + dx * reach, c.Y + dy * reach);
        }

        static RectangleF Bounds(VisualElement e)
        {
            switch (e.Kind)
            {
                case "box": case "mark": case "cylinder": return new RectangleF(e.X, e.Y, e.W, e.H);
                case "box3d": { PointF v = DepthVector(e); return RectangleF.FromLTRB(e.X, e.Y + v.Y, e.X + e.W + v.X, e.Y + e.H); }
                case "grid": return new RectangleF(e.X, e.Y, e.Cols * e.Cell, e.Rows * e.Cell);
                case "circle": return new RectangleF(e.X - e.R, e.Y - e.R, 2 * e.R, 2 * e.R);
                case "ellipse": return new RectangleF(e.X - e.Rx, e.Y - e.Ry, 2 * e.Rx, 2 * e.Ry);
                case "text": { float w = Math.Max(20, e.Text.Length * e.Size * 0.52f); return new RectangleF(e.X - w / 2, e.Y - e.Size * 0.7f, w, e.Size * 1.4f); }
                default:
                    {
                        if (e.Pts.Count == 0) return new RectangleF(e.X, e.Y, 1, 1);
                        float minX = e.Pts.Min(p => p.X), maxX = e.Pts.Max(p => p.X), minY = e.Pts.Min(p => p.Y), maxY = e.Pts.Max(p => p.Y);
                        return RectangleF.FromLTRB(minX, minY, maxX, maxY);
                    }
            }
        }

        /// <summary>A screenshot point, in board units (for an arrow from the board to the screen).</summary>
        PointF ShotToBoard(ScreenPoint p)
        {
            PointF px = ShotToPx(p.X, p.Y);
            return new PointF((px.X - board.X) / k, (px.Y - board.Y) / k);
        }

        // ------------------------------------------------------------------ outlines (board units)

        static List<PointF> RoundRect(float x, float y, float w, float h, float r)
        {
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
            var pts = new List<PointF>();
            Action<float, float, double> arc = (cx, cy, start) =>
            {
                for (int i = 0; i <= 8; i++)
                {
                    double a = start + i * Math.PI / 2 / 8;
                    pts.Add(new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r)));
                }
            };
            arc(x + w - r, y + r, -Math.PI / 2);
            arc(x + w - r, y + h - r, 0);
            arc(x + r, y + h - r, Math.PI / 2);
            arc(x + r, y + r, Math.PI);
            pts.Add(pts[0]);
            return pts;
        }

        static List<PointF> EllipsePts(float cx, float cy, float rx, float ry)
        {
            var pts = new List<PointF>();
            for (int i = 0; i <= 72; i++)
            {
                double a = -Math.PI / 2 + i * 2 * Math.PI / 72;
                pts.Add(new PointF(cx + (float)(Math.Cos(a) * rx), cy + (float)(Math.Sin(a) * ry)));
            }
            return pts;
        }

        /// <summary>Catmull-Rom through the points (closed: back to the start).</summary>
        static List<PointF> Smooth(List<PointF> p, bool closed)
        {
            if (p.Count < 3) return new List<PointF>(p);
            var outp = new List<PointF>();
            int n = p.Count, segs = closed ? n : n - 1;
            Func<int, PointF> at = i => closed ? p[((i % n) + n) % n] : p[Math.Max(0, Math.Min(n - 1, i))];
            for (int i = 0; i < segs; i++)
            {
                PointF p0 = at(i - 1), p1 = at(i), p2 = at(i + 1), p3 = at(i + 2);
                for (int j = 0; j < 12; j++)
                {
                    float t = j / 12f, t2 = t * t, t3 = t2 * t;
                    outp.Add(new PointF(
                        0.5f * (2 * p1.X + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3),
                        0.5f * (2 * p1.Y + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)));
                }
            }
            outp.Add(closed ? p[0] : p[n - 1]);
            return outp;
        }

        /// <summary>How far back (and up) a box3d's depth goes, in board units.</summary>
        static PointF DepthVector(VisualElement e)
        {
            float d = e.Depth > 0 ? e.Depth : Math.Min(e.W, e.H) * 0.5f;
            return new PointF(d * 0.66f, -d * 0.42f);
        }

        static List<PointF> Rotated(List<PointF> pts, PointF c, float degrees)
        {
            if (degrees == 0) return pts;
            double a = degrees * Math.PI / 180;
            for (int i = 0; i < pts.Count; i++) pts[i] = Rotate(pts[i], c, a);
            return pts;
        }

        /// <summary>A cylinder's silhouette and its near cap, unrotated (axis vertical), board units.</summary>
        static void CylinderShape(VisualElement e, out List<PointF> body, out List<PointF> cap, out PointF left, out PointF right)
        {
            // a horizontal one is drawn as a vertical one of swapped size, then turned a quarter
            float cx = e.X + e.W / 2, cy = e.Y + e.H / 2;
            float w = e.Horizontal ? e.H : e.W, h = e.Horizontal ? e.W : e.H;
            float x = cx - w / 2, y = cy - h / 2, rx = w / 2, ry = Math.Min(w * 0.17f, h * 0.25f);
            body = new List<PointF>();
            float topC = y + ry, botC = y + h - ry;
            for (int i = 0; i <= 24; i++) { double t = Math.PI - i * Math.PI / 24; body.Add(new PointF(cx + (float)(Math.Cos(t) * rx), botC + (float)(Math.Sin(t) * ry))); }
            for (int i = 0; i <= 24; i++) { double t = -i * Math.PI / 24; body.Add(new PointF(cx + (float)(Math.Cos(t) * rx), topC + (float)(Math.Sin(t) * ry))); }
            body.Add(body[0]);
            cap = EllipsePts(cx, topC, rx, ry);
            left = new PointF(x, cy); right = new PointF(x + w, cy);
            float rot = e.Rot + (e.Horizontal ? 90 : 0);
            var c = new PointF(cx, cy);
            Rotated(body, c, rot); Rotated(cap, c, rot);
            var lr = Rotated(new List<PointF> { left, right }, c, rot);
            left = lr[0]; right = lr[1];
        }

        /// <summary>The element's outline in board units, where it is now. Arrows: the shaft.</summary>
        List<PointF> Outline(VisualElement e, double now)
        {
            List<PointF> pts;
            switch (e.Kind)
            {
                case "box": pts = RoundRect(e.X, e.Y, e.W, e.H, e.R); break;
                case "box3d":
                    {
                        PointF v = DepthVector(e);
                        pts = new List<PointF> { new PointF(e.X, e.Y + e.H), new PointF(e.X, e.Y), new PointF(e.X + v.X, e.Y + v.Y), new PointF(e.X + e.W + v.X, e.Y + v.Y),
                            new PointF(e.X + e.W + v.X, e.Y + e.H + v.Y), new PointF(e.X + e.W, e.Y + e.H), new PointF(e.X, e.Y + e.H) };
                        break;
                    }
                case "cylinder":
                    {
                        List<PointF> body, cap; PointF l, r;
                        CylinderShape(e, out body, out cap, out l, out r);
                        return PlaceAll(e, body, now);
                    }
                case "circle": pts = EllipsePts(e.X, e.Y, e.R, e.R); break;
                case "ellipse": pts = EllipsePts(e.X, e.Y, e.Rx, e.Ry); break;
                case "grid": pts = RoundRect(e.X, e.Y, e.Cols * e.Cell, e.Rows * e.Cell, 6); break;
                case "poly":
                    {
                        var p = new List<PointF>(e.Pts);
                        if (e.Curve) pts = Smooth(p, true);
                        else { if (Dist(p[0], p[p.Count - 1]) > 0.5f) p.Add(p[0]); pts = p; }
                        break;
                    }
                case "line": pts = e.Curve ? Smooth(e.Pts, false) : new List<PointF>(e.Pts); break;
                case "arrow":
                    {
                        PointF a0 = Center(e.From, now), b0 = Center(e.To, now);
                        PointF a = Edge(e.From, b0, now), b = Edge(e.To, a0, now);
                        // an end fixed at a point moves with the arrow's own move (a push arrow pushing down)
                        PointF own = Offset(e, now);
                        if (e.From is PointF) a = new PointF(a.X + own.X, a.Y + own.Y);
                        if (e.To is PointF) b = new PointF(b.X + own.X, b.Y + own.Y);
                        pts = new List<PointF>();
                        float mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2, len = Dist(a, b);
                        float nx = len > 0 ? -(b.Y - a.Y) / len : 0, ny = len > 0 ? (b.X - a.X) / len : 0;
                        var c = new PointF(mx + nx * e.Bend * len * 0.5f, my + ny * e.Bend * len * 0.5f);
                        for (int i = 0; i <= 32; i++)
                        {
                            float t = i / 32f, u = 1 - t;
                            pts.Add(new PointF(u * u * a.X + 2 * u * t * c.X + t * t * b.X, u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y));
                        }
                        return pts;   // its ends already follow what they connect
                    }
                default: return new List<PointF>();
            }
            if (e.Rot != 0 && e.Kind != "box3d") Rotated(pts, BaseCenter(e), e.Rot);
            return PlaceAll(e, pts, now);
        }

        // ------------------------------------------------------------------ colours and type

        static readonly Dictionary<string, Color> Palette = new Dictionary<string, Color>
        {
            { "ink", Color.FromArgb(35, 33, 30) }, { "black", Color.FromArgb(35, 33, 30) },
            { "blue", Color.FromArgb(44, 104, 214) }, { "red", Color.FromArgb(219, 68, 58) },
            { "green", Color.FromArgb(42, 150, 86) }, { "yellow", Color.FromArgb(226, 170, 24) },
            { "orange", Color.FromArgb(236, 118, 38) }, { "purple", Color.FromArgb(118, 84, 204) },
            { "teal", Color.FromArgb(20, 148, 144) }, { "pink", Color.FromArgb(218, 84, 148) },
            { "gray", Color.FromArgb(128, 125, 118) }, { "grey", Color.FromArgb(128, 125, 118) },
            { "brown", Color.FromArgb(150, 100, 58) }, { "sky", Color.FromArgb(66, 160, 226) },
            { "white", Color.FromArgb(255, 255, 255) },
        };

        static readonly Color Paper = Color.FromArgb(251, 248, 241);
        static readonly Color PaperEdge = Color.FromArgb(226, 219, 203);
        static readonly Color Ink = Color.FromArgb(35, 33, 30);
        static readonly Color Muted = Color.FromArgb(132, 126, 115);

        static Color ColorOf(string name)
        {
            Color c;
            if (name != null && Palette.TryGetValue(name, out c)) return c;
            if (name != null && name.StartsWith("#") && (name.Length == 7 || name.Length == 4))
            {
                try { return ColorTranslator.FromHtml(name); } catch { }
            }
            return Palette["blue"];
        }

        static Color A(Color c, double alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(alpha * 255))), c.R, c.G, c.B); }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static bool Light(Color c) { return c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 170; }

        readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        Font FontFor(float px, bool bold)
        {
            float size = (float)Math.Round(Math.Max(13, px), 1);   // a board squeezed beside something stays readable
            string key = (bold ? "b" : "r") + size.ToString(CultureInfo.InvariantCulture);
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                try { f = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { f = new Font(FontFamily.GenericSansSerif, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }
                if (fonts.Count > 64) { foreach (Font old in fonts.Values) old.Dispose(); fonts.Clear(); }
                fonts[key] = f;
            }
            return f;
        }

        // ------------------------------------------------------------------ drawing

        RectangleF board;          // the board, in surface pixels
        float k = 1;               // surface pixels per board unit
        RectangleF screen;         // the monitor, in surface pixels (for marks)

        PointF Px(PointF b) { return new PointF(board.X + b.X * k, board.Y + b.Y * k); }

        PointF ShotToPx(float x, float y)
        {
            return new PointF(screen.X + x * screen.Width / Math.Max(1, ShotW), screen.Y + y * screen.Height / Math.Max(1, ShotH));
        }

        /// <summary>Draws the scene at `now` (scene seconds). boardRect: where the board goes; screenRect: the
        /// monitor the screenshot showed (both in g's pixels).</summary>
        public void Draw(Graphics g, RectangleF boardRect, RectangleF screenRect, double now)
        {
            board = boardRect;
            screen = screenRect;
            k = board.Width / BoardW;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;

            double vis = EaseOut((now - Shown) / 0.35);
            if (Closing >= 0) vis *= 1 - Clamp01((now - Closing) / 0.3);
            if (vis <= 0.001) return;
            float lift = (float)((1 - vis) * 14 * k);

            var saved = g.Save();
            g.TranslateTransform(0, lift);
            DrawBoard(g, now, vis);
            g.Restore(saved);
            if (Problem == null) foreach (VisualElement e in Elements) if (e.OnScreen) DrawMark(g, e, now, vis);
            // arrows that reach out to the screen are drawn on top of everything
            if (Problem == null) foreach (VisualElement e in Elements) if (e.Kind == "arrow" && (e.From is ScreenPoint || e.To is ScreenPoint || IsMark(e.From) || IsMark(e.To))) DrawElement(g, e, now, vis, true);
        }

        bool IsMark(object end) { VisualElement e = Find(end as string); return e != null && e.OnScreen; }

        void DrawBoard(Graphics g, double now, double vis)
        {
            RectangleF b = board;
            float rad = 18 * k;
            // soft shadow, then the paper
            for (int i = 6; i >= 1; i--)
            {
                float grow = i * 3.2f * k;
                using (GraphicsPath sp = RoundedPath(new RectangleF(b.X - grow * 0.6f, b.Y - grow * 0.3f + 7 * k, b.Width + grow * 1.2f, b.Height + grow * 1.0f), rad + grow))
                using (var br = new SolidBrush(A(Color.Black, 0.045 * vis * (7 - i) / 6.0)))
                    g.FillPath(br, sp);
            }
            using (GraphicsPath p = RoundedPath(b, rad))
            {
                using (var br = new SolidBrush(A(Paper, vis))) g.FillPath(br, p);
                using (var pen = new Pen(A(PaperEdge, vis), 1.4f * k)) g.DrawPath(pen, p);
            }
            var clip = g.Save();
            using (GraphicsPath p = RoundedPath(b, rad)) g.SetClip(p);

            // title, with a marker underline that draws in under it
            string title = Problem != null ? "hm." : Title.Length > 0 ? Title : "";
            Font tf = FontFor(29 * k, true);
            using (var br = new SolidBrush(A(Ink, vis))) g.DrawString(title, tf, br, Px(new PointF(36, 18)));
            if (title.Length > 0)
            {
                SizeF ts = g.MeasureString(title, tf);
                double u = EaseOut((now - Shown - 0.25) / 0.6);
                PointF u0 = Px(new PointF(38, 60));
                using (var pen = new Pen(A(Palette["orange"], 0.85 * vis), 4.5f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, u0, new PointF(u0.X + (float)(Math.Min(ts.Width * 0.6, 220 * k) * u), u0.Y));
            }
            // progress dots (top right): one per step
            if (Problem == null && Steps.Count > 0)
            {
                int n = Steps.Count;
                for (int i = 0; i < n; i++)
                {
                    PointF c = Px(new PointF(BoardW - 34 - (n - 1 - i) * 18, 38));
                    bool on = i <= Current;
                    float r = (i == Current && !Done ? 5.5f : 4.5f) * k;
                    using (var br = new SolidBrush(A(on ? Ink : PaperEdge, vis))) g.FillEllipse(br, c.X - r, c.Y - r, 2 * r, 2 * r);
                }
            }

            if (Problem != null)
            {
                using (var br = new SolidBrush(A(Ink, vis)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(Problem, FontFor(24 * k, false), br, new RectangleF(Px(new PointF(80, 120)), new SizeF(840 * k, 420 * k)), sf);
                g.Restore(clip);
                return;
            }

            // still being written: a pencil line that scribbles across, waiting
            if (Elements.Count(x => x.Appear >= 0) == 0 && !Done)
            {
                double ph = (now - Shown) * 1.6;
                float x0 = 350, w = 300;
                var pts = new List<PointF>();
                for (int i = 0; i <= 40; i++)
                {
                    float t = i / 40f;
                    pts.Add(Px(new PointF(x0 + w * t, 330 + (float)(Math.Sin(t * 14 + ph * 3) * 10 * Math.Sin(t * Math.PI)))));
                }
                double head = (ph % 1.6) / 1.2;
                using (var pen = new Pen(A(Muted, 0.55 * vis), 3 * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    DrawPartial(g, pen, pts, Math.Min(1, head));
                using (var br = new SolidBrush(A(Muted, vis)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(Current < 0 ? "drawing it…" : "", FontFor(19 * k, false), br, Px(new PointF(500, 372)), sf);
            }

            // shapes first, then every word on top, so no line ever runs through a label
            labelsLater = new List<Action>();
            foreach (VisualElement e in Elements)
                if (!e.OnScreen && !(e.Kind == "arrow" && (e.From is ScreenPoint || e.To is ScreenPoint || IsMark(e.From) || IsMark(e.To))))
                    DrawElement(g, e, now, vis, false);
            List<Action> words = labelsLater;
            labelsLater = null;
            foreach (Action a in words) a();

            // caption: what's being said right now
            string say = Current >= 0 && Current < Steps.Count ? Steps[Current].Say : "";
            if (say.Length > 0)
            {
                double ca = EaseOut((now - Steps[Current].Start) / 0.4) * vis;
                using (var pen = new Pen(A(PaperEdge, vis), 1.2f * k)) g.DrawLine(pen, Px(new PointF(36, 598)), Px(new PointF(BoardW - 36, 598)));
                using (var br = new SolidBrush(A(Mix(Ink, Paper, 0.12), ca)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                    g.DrawString(say, FontFor(Math.Max(20 * k, 15), false), br, new RectangleF(Px(new PointF(46, 604)), new SizeF((BoardW - 92) * k, 66 * k)), sf);
            }
            if (Footer.Length > 0)
            {
                // left of the step dots
                float right = BoardW - 34 - (Math.Max(1, Steps.Count) - 1) * 18 - 16;
                using (var br = new SolidBrush(A(Muted, vis)))
                using (var sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
                    g.DrawString(Footer, FontFor(15 * k, false), br, Px(new PointF(right, 38)), sf);
            }
            g.Restore(clip);
        }

        static GraphicsPath RoundedPath(RectangleF r, float rad)
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

        /// <summary>The first `frac` of a polyline (by length).</summary>
        static void DrawPartial(Graphics g, Pen pen, List<PointF> pts, double frac)
        {
            if (pts.Count < 2 || frac <= 0) return;
            if (frac >= 1) { g.DrawLines(pen, pts.ToArray()); return; }
            float total = 0;
            for (int i = 1; i < pts.Count; i++) total += Dist(pts[i - 1], pts[i]);
            float want = (float)(total * frac), got = 0;
            var part = new List<PointF> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                float seg = Dist(pts[i - 1], pts[i]);
                if (got + seg >= want)
                {
                    float t = seg > 0 ? (want - got) / seg : 0;
                    part.Add(new PointF(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t));
                    break;
                }
                got += seg;
                part.Add(pts[i]);
            }
            if (part.Count >= 2) g.DrawLines(pen, part.ToArray());
        }

        static PointF Rotate(PointF p, PointF c, double ang)
        {
            double s = Math.Sin(ang), co = Math.Cos(ang), dx = p.X - c.X, dy = p.Y - c.Y;
            return new PointF((float)(c.X + dx * co - dy * s), (float)(c.Y + dx * s + dy * co));
        }

        void DrawElement(Graphics g, VisualElement e, double now, double vis, bool topLayer)
        {
            if (e.Appear < 0 || now < e.Appear) return;
            double p = Clamp01((now - e.Appear) / e.AppearDur);
            double fade = vis;
            if (e.HideStart >= 0 && now >= e.HideStart) fade *= 1 - Clamp01((now - e.HideStart) / 0.45);
            if (fade <= 0.002) return;
            Color col = ColorOf(e.Color);

            // pulse: a quick swell and a halo
            float pulse = 0;
            if (e.PulseStart >= 0 && now >= e.PulseStart && now < e.PulseStart + 0.9) pulse = (float)Math.Sin((now - e.PulseStart) / 0.9 * Math.PI);

            if (e.Kind == "text")
            {
                if (labelsLater != null) labelsLater.Add(delegate { DrawText(g, e, now, p, fade, col); });
                else DrawText(g, e, now, p, fade, col);
                return;
            }
            if (e.Kind == "grid") { DrawGrid(g, e, now, p, fade, col); return; }

            List<PointF> outline = Outline(e, now);
            if (outline.Count < 2) return;
            var px = new List<PointF>(outline.Count);
            foreach (PointF b in outline) px.Add(topLayer ? PxAny(b) : Px(b));
            if (pulse > 0)
            {
                PointF c = Centroid(px);
                for (int i = 0; i < px.Count; i++) px[i] = new PointF(c.X + (px[i].X - c.X) * (1 + 0.07f * pulse), c.Y + (px[i].Y - c.Y) * (1 + 0.07f * pulse));
                using (var halo = new Pen(A(col, 0.22 * pulse * fade), (e.Width + 12) * k) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(halo, px.ToArray());
            }

            bool closed = e.Kind == "box" || e.Kind == "circle" || e.Kind == "ellipse" || e.Kind == "poly" || e.Kind == "box3d" || e.Kind == "cylinder";
            double strokeP = Ease(Math.Min(1, p / 0.7));
            double fillA = Clamp01((p - 0.4) / 0.6);
            Color fc = e.Fill == "solid" ? col : Mix(Paper, col, 0.2);
            if (closed && e.Fill != "none" && fillA > 0)
            {
                // a soft shadow under free-standing solid things: they sit on the paper instead of being printed on it
                if (e.Shadow)
                    using (var sp = new GraphicsPath())
                    {
                        var sh = new PointF[px.Count];
                        for (int i = 0; i < px.Count; i++) sh[i] = new PointF(px[i].X + 3 * k, px[i].Y + 7 * k);
                        sp.AddPolygon(sh);
                        using (var br = new SolidBrush(A(Color.Black, 0.09 * fillA * fade))) g.FillPath(br, sp);
                    }
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(px.ToArray());
                    if (e.Kind == "box3d") DrawBoxFaces(g, e, now, fc, fillA * fade);
                    else if (e.Kind == "cylinder") DrawCylinderFill(g, e, now, fc, fillA * fade, path);
                    else if (e.Shade) FillShaded(g, e, path, px, fc, fillA * fade);
                    else using (var br = new SolidBrush(A(fc, fillA * fade))) g.FillPath(br, path);
                }
            }
            Color stroke = e.Fill == "solid" && closed ? Mix(col, Ink, 0.3) : col;
            using (var pen = new Pen(A(stroke, fade), e.Width * k) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (e.Dash) pen.DashPattern = new float[] { 2.2f, 1.8f };
                DrawPartial(g, pen, px, strokeP);
                if (fillA > 0 && (e.Kind == "box3d" || e.Kind == "cylinder"))
                {
                    pen.Color = A(stroke, fade * fillA);
                    pen.Width = Math.Max(1, e.Width * 0.7f * k);
                    foreach (PointF[] edge in InnerEdges(e, now)) g.DrawLines(pen, edge);
                }
            }
            if (e.Kind == "arrow" && p > 0.55) DrawHead(g, px, col, e.Width * k, Clamp01((p - 0.55) / 0.3) * fade);

            // flow: dashes running along it (air, water, current, data)
            if (e.FlowStart >= 0 && now > e.FlowStart && p >= 1 && !closed)
            {
                using (var pen = new Pen(A(Mix(col, Color.White, 0.55), 0.95 * fade * Clamp01((now - e.FlowStart) / 0.5)), Math.Max(2, e.Width * 0.55f) * k))
                {
                    pen.DashPattern = new float[] { 2.5f, 4.5f };
                    pen.DashOffset = (float)(-(now - e.FlowStart) * 7 * e.FlowSpeed);
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.DashCap = DashCap.Round;
                    g.DrawLines(pen, px.ToArray());
                }
            }

            if (!string.IsNullOrEmpty(e.Label) && p > 0.5)
            {
                double la = Clamp01((p - 0.5) / 0.4) * fade;
                if (labelsLater != null) labelsLater.Add(delegate { DrawLabel(g, e, px, col, closed, la); });
                else DrawLabel(g, e, px, col, closed, la);
            }
        }

        PointF PxAny(PointF b) { return Px(b); }

        List<Action> labelsLater;   // while the board draws: the words, drawn after every shape

        // ------------------------------------------------------------------ shading and solids

        /// <summary>Light from the top left: a gradient across the shape (a ball gets a round highlight).</summary>
        void FillShaded(Graphics g, VisualElement e, GraphicsPath path, List<PointF> px, Color fc, double alpha)
        {
            RectangleF b = path.GetBounds();
            if (b.Width < 1 || b.Height < 1) return;
            Color light = Mix(fc, Color.White, 0.32), dark = Mix(fc, Ink, 0.22);
            if (e.Kind == "circle")
            {
                using (var pg = new PathGradientBrush(path))
                {
                    pg.CenterPoint = new PointF(b.X + b.Width * 0.36f, b.Y + b.Height * 0.32f);
                    pg.CenterColor = A(Mix(fc, Color.White, 0.55), alpha);
                    pg.SurroundColors = new[] { A(dark, alpha) };
                    pg.FocusScales = new PointF(0.15f, 0.15f);
                    g.FillPath(pg, path);
                }
                return;
            }
            using (var lg = new LinearGradientBrush(RectangleF.Inflate(b, 1, 1), A(light, alpha), A(dark, alpha), 70f))
            {
                var blend = new ColorBlend(3);
                blend.Colors = new[] { A(light, alpha), A(fc, alpha), A(dark, alpha) };
                blend.Positions = new[] { 0f, 0.45f, 1f };
                lg.InterpolationColors = blend;
                g.FillPath(lg, path);
            }
        }

        PointF[] PxPts(VisualElement e, double now, params PointF[] board)
        {
            var outp = new PointF[board.Length];
            for (int i = 0; i < board.Length; i++) outp[i] = Px(PlaceAt(e, board[i], now, 0));
            return outp;
        }

        /// <summary>A box3d's three faces: the front as is, the top lit, the side in shade.</summary>
        void DrawBoxFaces(Graphics g, VisualElement e, double now, Color fc, double alpha)
        {
            PointF v = DepthVector(e);
            float x = e.X, y = e.Y, w = e.W, h = e.H;
            PointF[] front = PxPts(e, now, new PointF(x, y), new PointF(x + w, y), new PointF(x + w, y + h), new PointF(x, y + h));
            PointF[] top = PxPts(e, now, new PointF(x, y), new PointF(x + v.X, y + v.Y), new PointF(x + w + v.X, y + v.Y), new PointF(x + w, y));
            PointF[] side = PxPts(e, now, new PointF(x + w, y), new PointF(x + w + v.X, y + v.Y), new PointF(x + w + v.X, y + h + v.Y), new PointF(x + w, y + h));
            using (var br = new SolidBrush(A(Mix(fc, Ink, 0.24), alpha))) g.FillPolygon(br, side);
            using (var br = new SolidBrush(A(Mix(fc, Color.White, 0.3), alpha))) g.FillPolygon(br, top);
            using (var lg = new LinearGradientBrush(new RectangleF(front[0].X, front[0].Y - 1, Math.Max(2, front[1].X - front[0].X), Math.Max(2, front[3].Y - front[0].Y + 2)),
                A(Mix(fc, Color.White, 0.1), alpha), A(Mix(fc, Ink, 0.08), alpha), 90f))
                g.FillPolygon(lg, front);
        }

        /// <summary>A cylinder: a body lit like a can (bright stripe, dark edges) and a lighter cap.</summary>
        void DrawCylinderFill(Graphics g, VisualElement e, double now, Color fc, double alpha, GraphicsPath body)
        {
            List<PointF> bodyPts, cap; PointF l, r;
            CylinderShape(e, out bodyPts, out cap, out l, out r);
            PointF pl = Px(PlaceAt(e, l, now, 0)), pr = Px(PlaceAt(e, r, now, 0));
            if (Math.Abs(pl.X - pr.X) + Math.Abs(pl.Y - pr.Y) > 1)
            {
                using (var lg = new LinearGradientBrush(pl, pr, A(fc, alpha), A(fc, alpha)))
                {
                    var blend = new ColorBlend(4);
                    blend.Colors = new[] { A(Mix(fc, Ink, 0.3), alpha), A(Mix(fc, Color.White, 0.38), alpha), A(fc, alpha), A(Mix(fc, Ink, 0.35), alpha) };
                    blend.Positions = new[] { 0f, 0.3f, 0.65f, 1f };
                    lg.InterpolationColors = blend;
                    lg.WrapMode = WrapMode.TileFlipXY;
                    g.FillPath(lg, body);
                }
            }
            var capPx = PlaceAll(e, cap, now).Select(Px).ToArray();
            using (var br = new SolidBrush(A(Mix(fc, Color.White, 0.22), alpha))) g.FillPolygon(br, capPx);
        }

        /// <summary>The edges inside a solid's outline (a box3d's front corner lines, a cylinder's cap rim).</summary>
        List<PointF[]> InnerEdges(VisualElement e, double now)
        {
            var list = new List<PointF[]>();
            if (e.Kind == "box3d")
            {
                PointF v = DepthVector(e);
                list.Add(PxPts(e, now, new PointF(e.X, e.Y), new PointF(e.X + e.W, e.Y), new PointF(e.X + e.W, e.Y + e.H)));
                list.Add(PxPts(e, now, new PointF(e.X + e.W, e.Y), new PointF(e.X + e.W + v.X, e.Y + v.Y)));
            }
            else if (e.Kind == "cylinder")
            {
                List<PointF> body, cap; PointF l, r;
                CylinderShape(e, out body, out cap, out l, out r);
                list.Add(PlaceAll(e, cap, now).Select(Px).ToArray());
            }
            return list;
        }

        /// <summary>True when smaller shapes sit inside this one (it's a whole drawn with its parts).</summary>
        bool HoldsOthers(VisualElement e)
        {
            RectangleF b = Bounds(e);
            float area = b.Width * b.Height;
            foreach (VisualElement o in Elements)
            {
                if (o == e || o.OnScreen || o.Kind == "arrow" || o.Kind == "line" || o.Appear < 0 || o.HideStart >= 0) continue;
                RectangleF ob = Bounds(o);
                PointF c = new PointF(ob.X + ob.Width / 2, ob.Y + ob.Height / 2);
                if (ob.Width * ob.Height < area * 0.6f && b.Contains(c)) return true;
            }
            return false;
        }

        static PointF Centroid(List<PointF> px)
        {
            float x = 0, y = 0;
            foreach (PointF p in px) { x += p.X; y += p.Y; }
            return new PointF(x / px.Count, y / px.Count);
        }

        static void DrawHead(Graphics g, List<PointF> px, Color col, float width, double alpha)
        {
            PointF tip = px[px.Count - 1];
            PointF back = px[Math.Max(0, px.Count - 4)];
            float dx = tip.X - back.X, dy = tip.Y - back.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;
            dx /= len; dy /= len;
            float size = Math.Max(10, width * 3.4f), half = size * 0.55f;
            var pts = new[]
            {
                new PointF(tip.X + dx * width * 0.6f, tip.Y + dy * width * 0.6f),
                new PointF(tip.X - dx * size - dy * half, tip.Y - dy * size + dx * half),
                new PointF(tip.X - dx * size * 0.72f, tip.Y - dy * size * 0.72f),
                new PointF(tip.X - dx * size + dy * half, tip.Y - dy * size - dx * half),
            };
            using (var br = new SolidBrush(A(col, alpha))) g.FillPolygon(br, pts);
        }

        void DrawLabel(Graphics g, VisualElement e, List<PointF> px, Color col, bool closed, double alpha)
        {
            float minX = px.Min(p => p.X), maxX = px.Max(p => p.X), minY = px.Min(p => p.Y), maxY = px.Max(p => p.Y);
            Font f = FontFor(e.Size * k, true);
            SizeF sz = g.MeasureString(e.Label, f);
            string at = e.LabelAt;
            if (closed && at.Length == 0) at = (maxX - minX) > sz.Width + 10 * k && (maxY - minY) > sz.Height + 4 * k ? "inside" : "below";
            if (!closed && at.Length == 0) at = "mid";
            // a whole with parts drawn inside it: its name goes in its top corner, out of their way
            if (at == "inside" && HoldsOthers(e)) at = "corner";
            PointF c;
            if (at == "corner") c = new PointF(minX + sz.Width / 2 + 12 * k, minY + sz.Height / 2 + 8 * k);
            else if (at == "inside") c = new PointF((minX + maxX) / 2, (minY + maxY) / 2);
            else if (at == "above") c = new PointF((minX + maxX) / 2, minY - sz.Height / 2 - 4 * k);
            else if (at == "below") c = new PointF((minX + maxX) / 2, maxY + sz.Height / 2 + 4 * k);
            else if (at == "left") c = new PointF(minX - sz.Width / 2 - 8 * k, (minY + maxY) / 2);
            else if (at == "right") c = new PointF(maxX + sz.Width / 2 + 8 * k, (minY + maxY) / 2);
            else c = px[px.Count / 2];
            Color text = Ink;
            if ((at == "inside" || at == "corner") && e.Fill == "solid") text = Light(col) ? Ink : Color.White;
            var rect = new RectangleF(c.X - sz.Width / 2, c.Y - sz.Height / 2, sz.Width, sz.Height);
            if (at != "inside" && at != "corner")
            {
                // on a line: a scrap of paper behind the words so the line doesn't run through them
                RectangleF back = RectangleF.Inflate(rect, 5 * k, 1 * k);
                using (GraphicsPath bp = RoundedPath(back, 6 * k))
                using (var br = new SolidBrush(A(Paper, alpha * 0.94))) g.FillPath(br, bp);
                text = Light(col) ? Ink : Mix(col, Ink, 0.35);   // a white or yellow thing's name still reads on paper
            }
            else if (e.Fill != "solid")
            {
                // inside a tinted or empty shape: a patch of its own tint, so a line drawn across it passes under the words
                RectangleF back = RectangleF.Inflate(rect, 4 * k, 0);
                using (GraphicsPath bp = RoundedPath(back, 5 * k))
                using (var br = new SolidBrush(A(e.Fill == "none" ? Paper : Mix(Paper, col, 0.2), alpha * 0.92))) g.FillPath(br, bp);
            }
            using (var br = new SolidBrush(A(text, alpha)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(e.Label, f, br, new RectangleF(rect.X - 2, rect.Y, rect.Width + 4, rect.Height), sf);
        }

        void DrawText(Graphics g, VisualElement e, double now, double p, double fade, Color col)
        {
            PointF placed = PlaceAt(e, new PointF(e.X, e.Y), now, 0);
            PointF at = Px(new PointF(placed.X, placed.Y + (float)((1 - EaseOut(p)) * 8)));
            Font f = FontFor(e.Size * k, e.Size >= 24);
            var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            float maxW = 520 * k;
            SizeF sz = g.MeasureString(e.Text, f, (int)maxW);
            RectangleF r;
            if (e.Align == "left") { sf.Alignment = StringAlignment.Near; r = new RectangleF(at.X, at.Y - sz.Height / 2, sz.Width + 4, sz.Height); }
            else if (e.Align == "right") { sf.Alignment = StringAlignment.Far; r = new RectangleF(at.X - sz.Width - 4, at.Y - sz.Height / 2, sz.Width + 4, sz.Height); }
            else { sf.Alignment = StringAlignment.Center; r = new RectangleF(at.X - sz.Width / 2 - 2, at.Y - sz.Height / 2, sz.Width + 4, sz.Height); }
            Color c = col == Palette["ink"] ? Ink : Mix(col, Ink, 0.15);
            using (var br = new SolidBrush(A(c, EaseOut(p) * fade))) g.DrawString(e.Text, f, br, r, sf);
            sf.Dispose();
        }

        void DrawGrid(Graphics g, VisualElement e, double now, double p, double fade, Color col)
        {
            PointF corner = PlaceAt(e, new PointF(e.X, e.Y), now, 0);
            var o = new PointF(corner.X - e.X, corner.Y - e.Y);   // a grid moves with its whole but doesn't turn
            int n = e.Rows * e.Cols;
            Font f = FontFor(Math.Min(e.Cell * 0.36f, 22) * k, true);
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                for (int r = 0; r < e.Rows; r++)
                    for (int c = 0; c < e.Cols; c++)
                    {
                        int idx = r * e.Cols + c;
                        double cp = Clamp01((p - 0.7 * idx / Math.Max(1, n)) / 0.3);
                        if (cp <= 0) continue;
                        bool hi = e.Hi.Contains(r + "," + c);
                        float pad = 3;
                        var cell = new RectangleF(Px(new PointF(e.X + o.X + c * e.Cell + pad, e.Y + o.Y + r * e.Cell + pad)), new SizeF((e.Cell - 2 * pad) * k, (e.Cell - 2 * pad) * k));
                        float grow = (float)((1 - EaseOut(cp)) * 0.25);
                        cell = RectangleF.Inflate(cell, -cell.Width * grow / 2, -cell.Height * grow / 2);
                        using (GraphicsPath cpth = RoundedPath(cell, 7 * k))
                        {
                            using (var br = new SolidBrush(A(hi ? col : Mix(Paper, col, 0.16), cp * fade))) g.FillPath(br, cpth);
                            using (var pen = new Pen(A(Mix(col, Ink, 0.15), cp * fade), 2f * k)) g.DrawPath(pen, cpth);
                        }
                        string v = e.Values != null && r < e.Values.Length && c < e.Values[r].Length ? e.Values[r][c] : "";
                        if (v.Length > 0)
                            using (var br = new SolidBrush(A(hi ? (Light(col) ? Ink : Color.White) : Ink, cp * fade))) g.DrawString(v, f, br, cell, sf);
                    }
            }
            if (!string.IsNullOrEmpty(e.Label) && p > 0.4)
            {
                using (var br = new SolidBrush(A(Mix(col, Ink, 0.35), Clamp01((p - 0.4) / 0.4) * fade)))
                    g.DrawString(e.Label, FontFor(e.Size * k, true), br, Px(new PointF(e.X + o.X, e.Y + o.Y - e.Size * 1.5f)));
            }
        }

        /// <summary>A ring around something on the real screen, with its tag.</summary>
        void DrawMark(Graphics g, VisualElement e, double now, double vis)
        {
            if (e.Appear < 0 || now < e.Appear) return;
            double p = Clamp01((now - e.Appear) / e.AppearDur);
            double fade = vis;
            if (e.HideStart >= 0 && now >= e.HideStart) fade *= 1 - Clamp01((now - e.HideStart) / 0.45);
            if (fade <= 0.002) return;
            Color col = ColorOf(e.Color);
            PointF a = ShotToPx(e.X, e.Y), b = ShotToPx(e.X + e.W, e.Y + e.H);
            float pad = 8;
            var r = RectangleF.FromLTRB(a.X - pad, a.Y - pad, b.X + pad, b.Y + pad);
            List<PointF> ring = RoundRect(r.X, r.Y, r.Width, r.Height, Math.Min(16, Math.Min(r.Width, r.Height) / 3));
            // glow that breathes for a few seconds after it lands
            double age = now - e.Appear;
            if (p >= 1 && age < 4)
            {
                double breathe = 0.5 + 0.5 * Math.Sin(age * 5);
                using (var glow = new Pen(A(col, 0.18 * breathe * fade * (1 - age / 4)), 14) { LineJoin = LineJoin.Round })
                    g.DrawLines(glow, ring.ToArray());
            }
            using (var under = new Pen(A(Color.White, 0.85 * fade), 7) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                DrawPartial(g, under, ring, Ease(Math.Min(1, p / 0.8)));
            using (var pen = new Pen(A(col, fade), 4) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                DrawPartial(g, pen, ring, Ease(Math.Min(1, p / 0.8)));
            if (!string.IsNullOrEmpty(e.Label) && p > 0.6)
            {
                double la = Clamp01((p - 0.6) / 0.4) * fade;
                Font f = FontFor(17, true);
                SizeF sz = g.MeasureString(e.Label, f);
                float ty = r.Y - sz.Height - 10 > screen.Y ? r.Y - sz.Height - 10 : r.Bottom + 8;
                var tag = new RectangleF(r.X, ty, sz.Width + 16, sz.Height + 6);
                using (GraphicsPath tp = RoundedPath(tag, 8))
                using (var br = new SolidBrush(A(col, la))) g.FillPath(br, tp);
                using (var br = new SolidBrush(A(Light(col) ? Ink : Color.White, la))) g.DrawString(e.Label, f, br, tag.X + 8, tag.Y + 3);
            }
        }

        public void Dispose()
        {
            foreach (Font f in fonts.Values) f.Dispose();
            fonts.Clear();
        }

        /// <summary>A line of narration's speaking time when there's no voice to wait for.</summary>
        public static double SpeakingTime(string say)
        {
            int words = (say ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return Math.Max(1.6, words / 2.6 + 0.6);
        }

        /// <summary>Short text summary for a follow-up (what was shown, so "explain more" knows what "this" is).</summary>
        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("title: ").Append(Title).Append('\n');
            for (int i = 0; i < Steps.Count; i++)
            {
                sb.Append("step ").Append(i + 1).Append(": \"").Append(Steps[i].Say).Append("\" drew: ");
                var names = new List<string>();
                foreach (object o in Steps[i].Items)
                {
                    var e = o as VisualElement;
                    if (e != null) names.Add(e.Kind + " " + e.Id + (string.IsNullOrEmpty(e.Label) ? "" : " (" + e.Label + ")") + (e.Kind == "text" ? " \"" + e.Text + "\"" : ""));
                    else { var m = (VisualMotion)o; names.Add(m.Kind + " " + m.Id); }
                }
                sb.Append(string.Join(", ", names.ToArray())).Append('\n');
            }
            return sb.ToString();
        }
    }
}
