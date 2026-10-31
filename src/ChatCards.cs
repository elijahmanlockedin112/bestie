// ChatCards.cs - the chat's cards: a reply that has a shape (weather, a schedule, a plan, a comparison, options, a
// rating, progress, a recipe...) comes as a ```ui block of JSON, and this draws it from a small kit of parts:
//   {"title":"...","sub":"...","accent":"blue","items":[ {"t":"stat"...}, {"t":"tiles"...}, ... ]}
// stat, tiles, kv, progress, rating, timeline, checklist, compare, choices, tags, term, bars, text, divider.
// Choices are buttons (the clicked one is sent as their reply); checklist items tick. Icons are drawn, not emoji.
// A card that doesn't parse is shown as its code. C# 5 only. UI thread only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace Bestie
{
    internal sealed partial class ChatView
    {
        Font bigFont, midFont;

        void CardFonts()
        {
            if (bigFont != null) return;
            bigFont = new Font("Segoe UI Semibold", 27f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            midFont = new Font("Segoe UI Semibold", 17f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        static readonly Dictionary<string, Color> Accents = new Dictionary<string, Color>
        {
            { "warm", Color.FromArgb(176, 84, 32) }, { "orange", Color.FromArgb(214, 106, 32) }, { "blue", Color.FromArgb(44, 104, 214) },
            { "green", Color.FromArgb(38, 140, 78) }, { "red", Color.FromArgb(206, 60, 50) }, { "purple", Color.FromArgb(118, 84, 204) },
            { "teal", Color.FromArgb(20, 140, 136) }, { "pink", Color.FromArgb(206, 74, 138) }, { "yellow", Color.FromArgb(196, 146, 16) },
            { "gray", Color.FromArgb(110, 104, 96) },
        };

        static Color AccentOf(string name)
        {
            Color c;
            if (name != null && Accents.TryGetValue(name.Trim().ToLowerInvariant(), out c)) return c;
            if (name != null && name.StartsWith("#")) { try { return ColorTranslator.FromHtml(name); } catch { } }
            return Accents["warm"];
        }

        static Color Tint(Color c, double amount) { return Color.FromArgb((int)(255 + (c.R - 255) * amount), (int)(255 + (c.G - 255) * amount), (int)(255 + (c.B - 255) * amount)); }

        static string Str(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        static IEnumerable<object> List(Dictionary<string, object> d, string key) { return Json.Arr(d, key); }

        int cardIndex;   // counts cards within the message being laid out (checklist ticks are keyed by it)

        int Card(List<Piece> outp, string json, int x, int y, int width, List<string> lines)
        {
            Dictionary<string, object> d = null;
            try { d = Json.Obj(json); } catch { }
            if (d == null) return Code(outp, lines, "json", 1, new HashSet<int>(), false, x, y, width);
            // one part written on its own ({"t":"term",...}) is a card holding just that part
            if (!d.ContainsKey("items") && (d.ContainsKey("t") || d.ContainsKey("type")))
                d = new Dictionary<string, object> { { "accent", Json.Str(d, "accent") }, { "items", new object[] { d } } };
            CardFonts();
            int card = cardIndex++;
            Color accent = AccentOf(Json.Str(d, "accent"));
            int pad = S(14), ix = x + pad, iw = width - pad * 2, cy = y + pad;
            var inner = new List<Piece>();
            string title = Json.Str(d, "title"), sub = Json.Str(d, "sub") ?? Json.Str(d, "subtitle");
            if (!string.IsNullOrEmpty(title))
            {
                inner.Add(new Piece { Kind = PK.Box, R = new Rectangle(ix, cy + S(5), S(4), midFont.Height - S(6)), Back = accent, Radius = S(2) });
                int used;
                int th = Inline(inner, title, ix + S(12), cy, iw - S(12), midFont, midFont, Ink, false, out used);
                cy += th;
                if (!string.IsNullOrEmpty(sub)) { cy += Inline(inner, sub, ix + S(12), cy - S(4), iw - S(12), small, smallBold, Muted, true, out used) - S(2); }
                cy += S(10);
            }
            int n = 0;
            foreach (object o in List(d, "items"))
            {
                var it = o as Dictionary<string, object>;
                if (it == null) continue;
                cy = Item(inner, it, ix, cy, iw, accent, card + ":" + n) + S(12);
                n++;
            }
            int h = cy - y + pad - S(12);
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y + S(2), width, h), Back = Color.FromArgb(236, 224, 206), Radius = S(12) });
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, h), Back = Color.White, Border = SandLine, Radius = S(12) });
            outp.AddRange(inner);
            return y + h;
        }

        int Item(List<Piece> o, Dictionary<string, object> it, int x, int y, int w, Color accent, string key)
        {
            string t = (Json.Str(it, "t") ?? Json.Str(it, "type") ?? "text").ToLowerInvariant();
            Color ac = it.ContainsKey("accent") ? AccentOf(Json.Str(it, "accent")) : accent;
            int used;
            switch (t)
            {
                case "stat":
                    {
                        string icon = Json.Str(it, "icon");
                        int gx = x;
                        if (!string.IsNullOrEmpty(icon))
                        {
                            int gs = S(44);
                            o.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, gs, gs), Back = Tint(ac, 0.12), Radius = S(12) });
                            o.Add(new Piece { Kind = PK.Glyph, R = new Rectangle(x + S(8), y + S(8), gs - S(16), gs - S(16)), Text = icon, Fore = ac });
                            gx = x + gs + S(12);
                        }
                        string value = Json.Str(it, "value") ?? "", label = Json.Str(it, "label") ?? "", delta = Json.Str(it, "delta");
                        int vw = W(value, bigFont);
                        o.Add(new Piece { Kind = PK.Text, R = new Rectangle(gx, y - S(4), vw + 4, bigFont.Height), Text = value, Font = bigFont, Fore = Ink });
                        if (!string.IsNullOrEmpty(delta))
                        {
                            bool down = delta.TrimStart().StartsWith("-") || delta.TrimStart().StartsWith("−") || delta.TrimStart().StartsWith("↓");
                            Color dc = Json.Bool(it, "good_down", false) ? (down ? AddInk : DelInk) : (down ? DelInk : AddInk);
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(gx + vw + S(8), y + S(6), W(delta, smallBold) + 4, bigFont.Height - S(10)), Text = delta, Font = smallBold, Fore = dc });
                        }
                        int ly = y + bigFont.Height - S(6);
                        int lh = label.Length > 0 ? Inline(o, label, gx, ly, x + w - gx, small, smallBold, Muted, true, out used) : 0;
                        return Math.Max(y + (string.IsNullOrEmpty(icon) ? 0 : S(44)), ly + lh);
                    }
                case "tiles":
                    {
                        var tiles = List(it, "tiles").OfType<Dictionary<string, object>>().ToList();
                        if (tiles.Count == 0) return y;
                        int per = Math.Max(1, Math.Min(tiles.Count, Math.Max(1, w / S(76))));
                        int gap = S(6), tw = (w - gap * (per - 1)) / per, rowY = y;
                        bool icons = tiles.Any(tl => tl.ContainsKey("icon"));
                        int th = S(10) + small.Height + (icons ? S(30) : S(2)) + midFont.Height + (tiles.Any(tl => tl.ContainsKey("small")) ? small.Height + S(2) : 0) + S(8);
                        for (int i = 0; i < tiles.Count; i++)
                        {
                            if (i > 0 && i % per == 0) rowY += th + gap;
                            var tl = tiles[i];
                            int tx = x + (i % per) * (tw + gap);
                            bool hi = Json.Bool(tl, "hi", false);
                            o.Add(new Piece { Kind = PK.Box, R = new Rectangle(tx, rowY, tw, th), Back = hi ? Tint(ac, 0.14) : Color.FromArgb(250, 244, 235), Border = hi ? Tint(ac, 0.45) : Color.Empty, Radius = S(10) });
                            int cy2 = rowY + S(8);
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(tx, cy2, tw, small.Height), Text = Json.Str(tl, "top") ?? "", Font = small, Fore = Muted, Choice = null, Radius = -3 });
                            cy2 += small.Height + S(2);
                            if (icons)
                            {
                                if (tl.ContainsKey("icon")) o.Add(new Piece { Kind = PK.Glyph, R = new Rectangle(tx + (tw - S(24)) / 2, cy2, S(24), S(24)), Text = Json.Str(tl, "icon"), Fore = hi ? ac : Muted });
                                cy2 += S(28);
                            }
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(tx, cy2, tw, midFont.Height), Text = Json.Str(tl, "big") ?? "", Font = midFont, Fore = Ink, Radius = -3 });
                            cy2 += midFont.Height;
                            if (tl.ContainsKey("small")) o.Add(new Piece { Kind = PK.Text, R = new Rectangle(tx, cy2, tw, small.Height), Text = Json.Str(tl, "small"), Font = small, Fore = Muted, Radius = -3 });
                        }
                        return rowY + th;
                    }
                case "kv":
                    {
                        int ry = y, lineH = LineH(body);
                        var rows = List(it, "rows").ToList();
                        for (int i = 0; i < rows.Count; i++)
                        {
                            var pair = rows[i] as object[];
                            if (pair == null) { var al = rows[i] as System.Collections.ArrayList; if (al != null) pair = al.ToArray(); }
                            string k = pair != null && pair.Length > 0 ? Str(pair[0]) : Str(rows[i]), v = pair != null && pair.Length > 1 ? Str(pair[1]) : "";
                            if (i > 0) o.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x, ry, w, Math.Max(1, S(1))), Back = Color.FromArgb(240, 230, 216) });
                            int kw = Math.Min(w / 2, W(k, body) + S(4));
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, ry + S(3), kw, lineH), Text = k, Font = body, Fore = Muted });
                            var vals = new List<Piece>();
                            int vh = Inline(vals, v, 0, 0, w - kw - S(12), bold, bold, Ink, true, out used);
                            foreach (Piece p in vals) { p.R.Offset(x + w - used, ry + S(3)); o.Add(p); }
                            ry += Math.Max(lineH, vh) + S(6);
                        }
                        return ry;
                    }
                case "progress":
                    {
                        double value = Json.Num(it, "value", 0), max = Json.Num(it, "max", 100);
                        double frac = max > 0 ? Math.Max(0, Math.Min(1, value / max)) : 0;
                        string label = Json.Str(it, "label") ?? "", note = Json.Str(it, "note") ?? (Math.Round(frac * 100) + "%");
                        o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, y, w - W(note, smallBold) - S(8), LineH(body)), Text = label, Font = body, Fore = Ink, Radius = -2 });
                        o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + w - W(note, smallBold) - S(2), y, W(note, smallBold) + S(2), LineH(body)), Text = note, Font = smallBold, Fore = ac });
                        int by = y + LineH(body) + S(2), bh = S(10);
                        o.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, by, w, bh), Back = Sand, Radius = bh / 2 });
                        if (frac > 0) o.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, by, Math.Max(bh, (int)(w * frac)), bh), Back = ac, Radius = bh / 2 });
                        return by + bh;
                    }
                case "rating":
                    {
                        int value = (int)Math.Round(Json.Num(it, "value", 0)), of = Math.Max(1, Math.Min(10, (int)Json.Num(it, "of", 7)));
                        string label = Json.Str(it, "label") ?? "";
                        int d = S(14), gap = S(6), lineH = LineH(body);
                        int lw = label.Length > 0 ? Math.Min(w - (d + gap) * of - S(60), W(label, body) + S(12)) : 0;
                        if (lw > 0) o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, y, lw, lineH), Text = label, Font = body, Fore = Ink, Radius = -2 });
                        int dx = x + lw;
                        for (int i = 0; i < of; i++)
                        {
                            var r = new Rectangle(dx + i * (d + gap), y + (lineH - d) / 2, d, d);
                            o.Add(new Piece { Kind = PK.Dot, R = r, Back = i < value ? ac : Color.White, Border = i < value ? Color.Empty : SandLine });
                        }
                        string score = value + " out of " + of;
                        o.Add(new Piece { Kind = PK.Text, R = new Rectangle(dx + of * (d + gap) + S(4), y, W(score, smallBold) + 4, lineH), Text = score, Font = smallBold, Fore = ac });
                        return y + lineH;
                    }
                case "timeline":
                    {
                        var items = List(it, "items").OfType<Dictionary<string, object>>().ToList();
                        int timeW = 0;
                        foreach (var ti in items) timeW = Math.Max(timeW, W(Json.Str(ti, "time") ?? "", smallBold));
                        timeW = Math.Min(timeW + S(10), w / 3);
                        int lineX = x + timeW + S(16), ry = y, lineH = LineH(body);
                        var dots = new List<Piece>();
                        int firstMid = -1, lastMid = -1;
                        foreach (var ti in items)
                        {
                            string state = (Json.Str(ti, "state") ?? "next").ToLowerInvariant();
                            bool done = state == "done", now = state == "now";
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, ry, timeW, lineH), Text = Json.Str(ti, "time") ?? "", Font = now ? smallBold : small, Fore = now ? ac : Muted, Radius = -1 });
                            int mid = ry + lineH / 2;
                            if (firstMid < 0) firstMid = mid;
                            lastMid = mid;
                            int dd = now ? S(14) : S(10);
                            dots.Add(new Piece { Kind = PK.Dot, R = new Rectangle(lineX - dd / 2, mid - dd / 2, dd, dd), Back = done ? ac : now ? Color.White : Color.White, Border = done ? Color.Empty : now ? ac : SandLine });
                            var tp = new List<Piece>();
                            int th = Inline(tp, Json.Str(ti, "text") ?? "", lineX + S(16), ry, x + w - lineX - S(16), now ? bold : body, bold, done ? Muted : Ink, true, out used);
                            o.AddRange(tp);
                            string noteText = Json.Str(ti, "note");
                            if (!string.IsNullOrEmpty(noteText)) th += Inline(o, noteText, lineX + S(16), ry + th - S(4), x + w - lineX - S(16), small, smallBold, Muted, true, out used) - S(2);
                            ry += th + S(4);
                        }
                        if (firstMid >= 0 && lastMid > firstMid) o.Add(new Piece { Kind = PK.Bar, R = new Rectangle(lineX - Math.Max(1, S(1)), firstMid, Math.Max(2, S(2)), lastMid - firstMid), Back = Tint(ac, 0.3) });
                        o.AddRange(dots);
                        return ry - S(4);
                    }
                case "checklist":
                    {
                        var items = List(it, "items").ToList();
                        var doneIdx = new HashSet<int>(List(it, "done").Select(v => (int)Json.Num(new Dictionary<string, object> { { "v", v } }, "v", -1)));
                        int ry = y, lineH = LineH(body), box = S(18);
                        for (int i = 0; i < items.Count; i++)
                        {
                            string tick = key + ":" + i;
                            bool on = doneIdx.Contains(i) ^ (laying != null && laying.Ticked.Contains(tick));
                            int tx = x + box + S(10);
                            var tp = new List<Piece>();
                            int th = Inline(tp, Str(items[i]), tx, ry, w - box - S(10), body, bold, on ? Muted : Ink, true, out used);
                            var r = new Rectangle(x, ry + (lineH - box) / 2, box, box);
                            o.Add(new Piece { Kind = PK.Box, R = new Rectangle(x - S(2), ry, w + S(4), th), Back = Color.Transparent, Tick = tick });
                            o.Add(new Piece { Kind = PK.Box, R = r, Back = on ? ac : Color.White, Border = on ? Color.Empty : SandLine, Radius = S(5) });
                            if (on) o.Add(new Piece { Kind = PK.Glyph, R = Rectangle.Inflate(r, -S(3), -S(3)), Text = "tick", Fore = Color.White });
                            foreach (Piece p in tp) { if (on && p.Kind == PK.Text) p.Strike = true; o.Add(p); }
                            ry += Math.Max(lineH, th) + S(4);
                        }
                        return ry - S(4);
                    }
                case "compare":
                    {
                        var cols = List(it, "cols").OfType<Dictionary<string, object>>().Take(3).ToList();
                        if (cols.Count == 0) return y;
                        int gap = S(8), cw = (w - gap * (cols.Count - 1)) / cols.Count;
                        bool stack = cw < S(130);
                        if (stack) cw = w;
                        int cy = y, rowTop = y, tallest = 0;
                        var boxes = new List<Tuple<int, int, int, bool>>();   // x, y, height, pick
                        var contents = new List<Piece>();
                        for (int i = 0; i < cols.Count; i++)
                        {
                            var col = cols[i];
                            bool pick = Json.Bool(col, "pick", false);
                            int cx = stack ? x : x + i * (cw + gap), top = stack ? cy : rowTop;
                            int ty = top + S(10);
                            ty += Inline(contents, Json.Str(col, "title") ?? "", cx + S(10), ty, cw - S(20) - (pick ? S(44) : 0), bold, bold, Ink, false, out used);
                            if (pick)
                            {
                                int pw = W("pick", smallBold) + S(12);
                                contents.Add(new Piece { Kind = PK.Box, R = new Rectangle(cx + cw - pw - S(8), top + S(10), pw, smallBold.Height + S(4)), Back = ac, Radius = S(8) });
                                contents.Add(new Piece { Kind = PK.Text, R = new Rectangle(cx + cw - pw - S(8), top + S(10), pw, smallBold.Height + S(4)), Text = "pick", Font = smallBold, Fore = Color.White });
                            }
                            foreach (object pt in List(col, "points"))
                            {
                                string text = Str(pt);
                                bool minus = text.StartsWith("-") || text.StartsWith("−");
                                bool plus = text.StartsWith("+");
                                if (plus || minus) text = text.Substring(1).Trim();
                                int dotY = ty + LineH(small) / 2 - S(3);
                                if (plus || minus) contents.Add(new Piece { Kind = PK.Glyph, R = new Rectangle(cx + S(10), dotY - S(2), S(10), S(10)), Text = plus ? "plus" : "minus", Fore = plus ? AddInk : DelInk });
                                else contents.Add(new Piece { Kind = PK.Dot, R = new Rectangle(cx + S(12), dotY, S(5), S(5)), Back = Muted });
                                ty += Inline(contents, text, cx + S(26), ty, cw - S(36), small, smallBold, Ink, true, out used) + S(2);
                            }
                            int hgt = ty - top + S(8);
                            boxes.Add(Tuple.Create(cx, top, hgt, pick));
                            tallest = Math.Max(tallest, hgt);
                            if (stack) cy += hgt + gap;
                        }
                        foreach (var b in boxes)
                            o.Add(new Piece { Kind = PK.Box, R = new Rectangle(b.Item1, b.Item2, cw, stack ? b.Item3 : tallest), Back = b.Item4 ? Tint(ac, 0.08) : Color.FromArgb(250, 244, 235), Border = b.Item4 ? ac : Color.Empty, Radius = S(10) });
                        o.AddRange(contents);
                        return stack ? cy - gap : rowTop + tallest;
                    }
                case "choices":
                case "buttons":
                    {
                        var opts = List(it, "options").Select(Str).Where(s => s.Length > 0).ToList();
                        int bx = x, by = y, bh = body.Height + S(12);
                        string chosen = laying != null ? laying.Chosen : null;
                        foreach (string opt in opts)
                        {
                            int bw = Math.Min(w, W(opt, bold) + S(28));
                            if (bx + bw > x + w && bx > x) { bx = x; by += bh + S(6); }
                            bool picked = chosen == opt, faded = chosen != null && !picked;
                            o.Add(new Piece { Kind = PK.Box, R = new Rectangle(bx, by, bw, bh), Back = picked ? ac : Color.White, Border = faded ? SandLine : ac, Radius = bh / 2, Choice = opt });
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(bx, by, bw, bh), Text = opt, Font = bold, Fore = picked ? Color.White : faded ? Muted : ac, Radius = -3 });
                            bx += bw + S(6);
                        }
                        return by + bh;
                    }
                case "tags":
                    {
                        int bx = x, by = y, th = small.Height + S(8);
                        foreach (object tg in List(it, "items"))
                        {
                            string tx = Str(tg);
                            int tw = W(tx, small) + S(16);
                            if (bx + tw > x + w && bx > x) { bx = x; by += th + S(6); }
                            o.Add(new Piece { Kind = PK.Box, R = new Rectangle(bx, by, tw, th), Back = Tint(ac, 0.1), Radius = th / 2 });
                            o.Add(new Piece { Kind = PK.Text, R = new Rectangle(bx, by, tw, th), Text = tx, Font = small, Fore = ac, Radius = -3 });
                            bx += tw + S(6);
                        }
                        return by + th;
                    }
                case "term":
                    {
                        string word = Json.Str(it, "word") ?? "", how = Json.Str(it, "how") ?? Json.Str(it, "kind");
                        int ty = y;
                        o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, ty, W(word, midFont) + 4, midFont.Height), Text = word, Font = midFont, Fore = ac });
                        if (!string.IsNullOrEmpty(how)) o.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + W(word, midFont) + S(10), ty + S(3), W(how, note) + 4, midFont.Height - S(3)), Text = how, Font = note, Fore = Muted });
                        ty += midFont.Height + S(2);
                        string sayIt = Json.Str(it, "sound") ?? Json.Str(it, "pronounced");
                        if (!string.IsNullOrEmpty(sayIt)) ty += Inline(o, "/" + sayIt + "/", x, ty - S(2), w, small, small, Muted, false, out used);
                        ty += Inline(o, Json.Str(it, "means") ?? Json.Str(it, "say") ?? "", x, ty, w, body, bold, Ink, true, out used);
                        string ex = Json.Str(it, "example");
                        if (!string.IsNullOrEmpty(ex)) ty += Inline(o, "“" + ex + "”", x, ty + S(2), w, note, note, Muted, false, out used) + S(2);
                        return ty;
                    }
                case "bars":
                    {
                        var lines = new List<string>();
                        foreach (object r in List(it, "rows"))
                        {
                            var pair = r as object[];
                            if (pair == null) { var al = r as System.Collections.ArrayList; if (al != null) pair = al.ToArray(); }
                            if (pair != null && pair.Length >= 2) lines.Add(Str(pair[0]) + ": " + Str(pair[1]) + (pair.Length > 2 ? " " + Str(pair[2]) : ""));
                        }
                        var bars = new List<Piece>();
                        int end = Chart(bars, lines, x, y, w);
                        if (bars.Count > 0) bars.RemoveAt(0);   // the chart's own card: this one already has one
                        o.AddRange(bars);
                        return end - S(8);
                    }
                case "divider":
                    o.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x, y + S(2), w, Math.Max(1, S(1))), Back = SandLine });
                    return y + S(4);
                default:
                    // text inside a card gets the whole markdown treatment: numbered steps, bullets, tips, code
                    return Blocks(o, Json.Str(it, "text") ?? "", x, y, w);
            }
        }

        // ------------------------------------------------------------------ icons (line drawings, never emoji)

        static void DrawGlyph(Graphics g, string name, Rectangle r, Color c)
        {
            float s = Math.Min(r.Width, r.Height) / 24f, ox = r.X + (r.Width - 24 * s) / 2f, oy = r.Y + (r.Height - 24 * s) / 2f;
            Func<float, float, PointF> P = (px, py) => new PointF(ox + px * s, oy + py * s);
            Func<float, float, float, float, RectangleF> R = (px, py, pw, ph) => new RectangleF(ox + px * s, oy + py * s, pw * s, ph * s);
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, Math.Max(1.4f, 1.9f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                switch ((name ?? "").ToLowerInvariant())
                {
                    case "sun": case "sunny": case "clear":
                        g.DrawEllipse(pen, R(7, 7, 10, 10));
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(pen, P(12 + (float)Math.Cos(a) * 8.5f, 12 + (float)Math.Sin(a) * 8.5f), P(12 + (float)Math.Cos(a) * 11f, 12 + (float)Math.Sin(a) * 11f));
                        }
                        break;
                    case "cloud": case "cloudy":
                        Cloud(g, pen, P, R, 0);
                        break;
                    case "rain": case "rainy":
                        Cloud(g, pen, P, R, -3);
                        g.DrawLine(pen, P(8, 18), P(7, 21)); g.DrawLine(pen, P(12, 18), P(11, 21)); g.DrawLine(pen, P(16, 18), P(15, 21));
                        break;
                    case "storm": case "bolt": case "lightning":
                        g.DrawLines(pen, new[] { P(13, 2), P(6, 13), P(12, 13), P(10, 22), P(18, 10), P(12, 10), P(13, 2) });
                        break;
                    case "snow":
                        for (int i = 0; i < 3; i++) { double a = i * Math.PI / 3; g.DrawLine(pen, P(12 - (float)Math.Cos(a) * 9, 12 - (float)Math.Sin(a) * 9), P(12 + (float)Math.Cos(a) * 9, 12 + (float)Math.Sin(a) * 9)); }
                        break;
                    case "moon": case "night":
                        using (var p = new GraphicsPath())
                        {
                            p.AddArc(R(4, 3, 18, 18), 60, 250);
                            p.AddArc(R(9, 1, 14, 14), 230, -170);
                            g.DrawPath(pen, p);
                        }
                        break;
                    case "clock": case "time":
                        g.DrawEllipse(pen, R(3, 3, 18, 18)); g.DrawLines(pen, new[] { P(12, 7), P(12, 12), P(15.5f, 14) });
                        break;
                    case "calendar": case "date":
                        g.DrawRectangle(pen, ox + 3 * s, oy + 5 * s, 18 * s, 16 * s); g.DrawLine(pen, P(3, 10), P(21, 10)); g.DrawLine(pen, P(8, 3), P(8, 7)); g.DrawLine(pen, P(16, 3), P(16, 7));
                        break;
                    case "check": case "done": case "tick":
                        g.DrawLines(pen, new[] { P(4.5f, 12.5f), P(9.5f, 17.5f), P(19.5f, 6.5f) });
                        break;
                    case "music": case "song":
                        g.DrawLine(pen, P(9, 18), P(9, 5)); g.DrawLine(pen, P(9, 5), P(19, 3)); g.DrawLine(pen, P(19, 3), P(19, 16));
                        g.FillEllipse(br, R(4, 15, 6, 5)); g.FillEllipse(br, R(14, 13, 6, 5));
                        break;
                    case "star":
                        {
                            var pts = new PointF[10];
                            for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float rr = i % 2 == 0 ? 10 : 4.3f; pts[i] = P(12 + (float)Math.Cos(a) * rr, 12.5f + (float)Math.Sin(a) * rr); }
                            g.DrawPolygon(pen, pts);
                            break;
                        }
                    case "heart":
                        using (var p = new GraphicsPath())
                        {
                            p.AddBezier(P(12, 20), P(2, 13), P(4, 3), P(12, 8));
                            p.AddBezier(P(12, 8), P(20, 3), P(22, 13), P(12, 20));
                            g.DrawPath(pen, p);
                        }
                        break;
                    case "pin": case "place": case "location":
                        using (var p = new GraphicsPath())
                        {
                            p.AddArc(R(5, 2, 14, 14), 140, 260);
                            p.AddLine(P(17.4f, 13.5f), P(12, 21.5f));
                            p.AddLine(P(12, 21.5f), P(6.6f, 13.5f));
                            g.DrawPath(pen, p);
                        }
                        g.DrawEllipse(pen, R(9.5f, 6.5f, 5, 5));
                        break;
                    case "book": case "study":
                        g.DrawLines(pen, new[] { P(12, 6), P(4, 4), P(4, 19), P(12, 21), P(20, 19), P(20, 4), P(12, 6), P(12, 21) });
                        break;
                    case "bulb": case "idea":
                        g.DrawArc(pen, R(5, 2, 14, 14), 140, 260);
                        g.DrawLine(pen, P(8.5f, 14.5f), P(9.5f, 17)); g.DrawLine(pen, P(15.5f, 14.5f), P(14.5f, 17));
                        g.DrawLine(pen, P(9.5f, 18), P(14.5f, 18)); g.DrawLine(pen, P(10.5f, 21), P(13.5f, 21));
                        break;
                    case "fire": case "streak":
                        using (var p = new GraphicsPath())
                        {
                            p.AddBezier(P(12, 22), P(4, 21), P(4, 12), P(9, 7));
                            p.AddBezier(P(9, 7), P(9, 11), P(11, 11), P(12, 2));
                            p.AddBezier(P(12, 2), P(18, 8), P(21, 18), P(12, 22));
                            g.DrawPath(pen, p);
                        }
                        break;
                    case "plus":
                        g.DrawLine(pen, P(12, 4), P(12, 20)); g.DrawLine(pen, P(4, 12), P(20, 12));
                        break;
                    case "minus":
                        g.DrawLine(pen, P(4, 12), P(20, 12));
                        break;
                    case "warn": case "warning": case "alert":
                        g.DrawPolygon(pen, new[] { P(12, 3), P(22, 20), P(2, 20) }); g.DrawLine(pen, P(12, 9), P(12, 14)); g.FillEllipse(br, R(11, 16, 2, 2));
                        break;
                    case "money": case "dollar": case "price":
                        g.DrawEllipse(pen, R(3, 3, 18, 18));
                        g.DrawBezier(pen, P(15.5f, 8.5f), P(13, 6.5f), P(8, 7.5f), P(9.5f, 11)); g.DrawBezier(pen, P(9.5f, 11), P(11, 13), P(15.5f, 12), P(14.5f, 15.5f));
                        g.DrawBezier(pen, P(14.5f, 15.5f), P(13, 17.5f), P(10, 17), P(8.5f, 15.5f)); g.DrawLine(pen, P(12, 5), P(12, 19));
                        break;
                    default:
                        g.DrawEllipse(pen, R(5, 5, 14, 14));
                        break;
                }
            }
            g.Restore(st);
        }

        static void Cloud(Graphics g, Pen pen, Func<float, float, PointF> P, Func<float, float, float, float, RectangleF> R, float up)
        {
            using (var p = new GraphicsPath())
            {
                p.AddArc(R(2, 9 + up, 9, 9), 90, 180);
                p.AddArc(R(6, 4 + up, 11, 11), 180, 180);
                p.AddArc(R(14, 8 + up, 8, 8), 270, 180);
                p.CloseFigure();
                g.DrawPath(pen, p);
            }
        }
    }
}
