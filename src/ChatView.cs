// ChatView.cs - the chat window's history, drawn by hand so a reply can be more than a paragraph: code cards
// (syntax colours, real line numbers, a highlighted line, copy), fix cards (diffs), steps, tips, tables, little bar
// charts, key caps for shortcuts and colour swatches next to colours. The assistant writes plain markdown plus a few
// fenced extras (see Assistant.cs, "the chat window draws"); anything it doesn't recognise is just text.
// C# 5 only. UI thread only.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Bestie
{
    internal sealed class ChatView : Control
    {
        // ------------------------------------------------------------------ palette (bestie's warm paper)

        static readonly Color Cream = Color.FromArgb(255, 250, 242);
        static readonly Color Sand = Color.FromArgb(244, 233, 218);
        static readonly Color SandLine = Color.FromArgb(226, 211, 190);
        static readonly Color Ink = Color.FromArgb(52, 46, 40);
        static readonly Color Warm = Color.FromArgb(176, 84, 32);
        static readonly Color Muted = Color.FromArgb(134, 122, 110);
        static readonly Color ChipBack = Color.FromArgb(240, 228, 212);
        static readonly Color ChipInk = Color.FromArgb(120, 52, 18);
        static readonly Color CodeBack = Color.FromArgb(43, 38, 34);
        static readonly Color CodeHead = Color.FromArgb(56, 50, 45);
        static readonly Color CodeInk = Color.FromArgb(237, 230, 220);
        static readonly Color CodeMuted = Color.FromArgb(140, 131, 120);
        static readonly Color CodeMark = Color.FromArgb(78, 60, 44);
        static readonly Color AddBack = Color.FromArgb(226, 241, 223), AddInk = Color.FromArgb(32, 110, 52);
        static readonly Color DelBack = Color.FromArgb(250, 226, 222), DelInk = Color.FromArgb(166, 44, 34);

        // syntax colours on the dark code card
        static readonly Color SynKeyword = Color.FromArgb(242, 166, 90);
        static readonly Color SynString = Color.FromArgb(168, 209, 141);
        static readonly Color SynComment = Color.FromArgb(140, 131, 120);
        static readonly Color SynNumber = Color.FromArgb(230, 192, 123);
        static readonly Color SynTag = Color.FromArgb(127, 184, 230);
        static readonly Color SynAttr = Color.FromArgb(215, 166, 232);

        // ------------------------------------------------------------------ messages

        sealed class Entry
        {
            public string Who;            // "you", "creature", "note"
            public string Name;           // the creature's name at the time
            public string Text;
            public int LaidWidth = -1;
            public int Height;
            public readonly List<Piece> Pieces = new List<Piece>();
        }

        enum PK { Text, Box, Bar, Dot }

        sealed class Piece
        {
            public PK Kind;
            public Rectangle R;
            public string Text;
            public Font Font;
            public Color Fore, Back, Border;
            public int Radius;
            public string Link, Copy;     // clickable: a url, or text to copy
            public bool Underline;
        }

        readonly List<Entry> entries = new List<Entry>();
        readonly float scale;
        readonly Font body, bold, small, smallBold, note, mono, monoSmall, head;
        int scrollY, contentH;
        bool dragThumb;
        int dragFrom, dragScroll;
        Piece copied;
        DateTime copiedAt;
        readonly Timer copyTimer = new Timer();
        readonly Dictionary<string, int> widths = new Dictionary<string, int>();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        Entry menuEntry;

        public event Action<string> LinkClicked;

        int S(double px) { return (int)Math.Round(px * scale); }

        public ChatView(float scale)
        {
            this.scale = scale;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Cream;
            body = new Font("Segoe UI", 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            bold = new Font("Segoe UI", 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            head = new Font("Segoe UI Semibold", 15f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            small = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            smallBold = new Font("Segoe UI", 12f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            note = new Font("Segoe UI", 12.5f * scale, FontStyle.Italic, GraphicsUnit.Pixel);
            mono = MonoFont(13f * scale);
            monoSmall = MonoFont(12.5f * scale);
            copyTimer.Interval = 1300;
            copyTimer.Tick += delegate { copyTimer.Stop(); copied = null; Invalidate(); };
            menu.Items.Add("copy this message", null, delegate { if (menuEntry != null) CopyText(menuEntry.Text); });
            menu.Items.Add("copy the whole chat", null, delegate { CopyText(PlainText); });
        }

        static Font MonoFont(float px)
        {
            foreach (string name in new[] { "Cascadia Mono", "Cascadia Code", "Consolas" })
            {
                try
                {
                    var f = new Font(name, px, FontStyle.Regular, GraphicsUnit.Pixel);
                    if (f.Name == name) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericMonospace, px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                copyTimer.Dispose(); menu.Dispose();
                foreach (Font f in new[] { body, bold, head, small, smallBold, note, mono, monoSmall }) f.Dispose();
            }
            base.Dispose(disposing);
        }

        public void Add(string who, string name, string text)
        {
            bool atBottom = scrollY >= contentH - ClientSize.Height - S(24);
            entries.Add(new Entry { Who = who, Name = name, Text = text ?? "" });
            if (entries.Count > 200) entries.RemoveRange(0, entries.Count - 200);
            Relayout();
            if (atBottom || who.StartsWith("you")) ScrollToEnd();
            Invalidate();
        }

        public string PlainText
        {
            get
            {
                var sb = new StringBuilder();
                foreach (Entry e in entries)
                {
                    if (e.Who == "note") sb.Append("   ").Append(e.Text).Append('\n');
                    else sb.Append(e.Who.StartsWith("you") ? e.Who : e.Name).Append(": ").Append(e.Text).Append('\n');
                }
                return sb.ToString();
            }
        }

        public void ScrollToEnd() { scrollY = Math.Max(0, contentH - ClientSize.Height); Invalidate(); }

        static void CopyText(string text)
        {
            try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch { }
        }

        // ------------------------------------------------------------------ layout

        int Pad { get { return S(12); } }
        int BarW { get { return S(8); } }
        int InnerWidth { get { return Math.Max(S(120), ClientSize.Width - Pad * 2 - BarW); } }

        void Relayout()
        {
            int w = InnerWidth;
            int y = S(8);
            foreach (Entry e in entries)
            {
                if (e.LaidWidth != w) Lay(e, w);
                y += e.Height + S(14);
            }
            contentH = y;
            scrollY = Math.Max(0, Math.Min(scrollY, Math.Max(0, contentH - ClientSize.Height)));
        }

        protected override void OnResize(EventArgs e)
        {
            bool atBottom = scrollY >= contentH - ClientSize.Height - S(24);
            base.OnResize(e);
            Relayout();
            if (atBottom) ScrollToEnd();
        }

        void Lay(Entry e, int width)
        {
            e.Pieces.Clear();
            e.LaidWidth = width;
            if (e.Who == "note")
            {
                int used;
                e.Height = Inline(e.Pieces, e.Text, S(10), 0, width - S(20), note, note, Muted, false, out used);
                return;
            }
            if (e.Who.StartsWith("you"))
            {
                // their words in a bubble on the right
                int max = (int)(width * 0.8), used;
                var inner = new List<Piece>();
                int h = Inline(inner, e.Text, 0, 0, max - S(24), body, bold, Ink, false, out used);
                int bw = used + S(24), bx = width - bw;
                e.Pieces.Add(new Piece { Kind = PK.Box, R = new Rectangle(bx, 0, bw, h + S(14)), Back = Sand, Radius = S(14) });
                foreach (Piece p in inner) { p.R.Offset(bx + S(12), S(7)); e.Pieces.Add(p); }
                e.Height = h + S(14);
                return;
            }
            int y = 0;
            e.Pieces.Add(new Piece { Kind = PK.Text, R = new Rectangle(0, 0, width, small.Height), Text = (e.Name ?? "bestie").ToLowerInvariant(), Font = smallBold, Fore = Warm });
            y += small.Height + S(3);
            y = Blocks(e.Pieces, e.Text, 0, y, width);
            e.Height = y;
        }

        // ------------------------------------------------------------------ blocks (markdown, line by line)

        static readonly Regex FenceOpen = new Regex(@"^\s*```\s*([A-Za-z0-9_+#.\-]*)(?::(\d+))?\s*(?:\{([\d,\s\-]+)\})?\s*$");
        static readonly Regex Numbered = new Regex(@"^\s*(\d{1,2})[.)]\s+(.*)$");
        static readonly Regex Bulleted = new Regex(@"^\s*[-*•]\s+(.*)$");
        static readonly Regex Heading = new Regex(@"^\s*#{1,4}\s+(.*)$");

        int Blocks(List<Piece> outp, string text, int x, int y, int width)
        {
            string[] lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var para = new StringBuilder();
            int gap = S(8);
            bool first = true;
            Func<int, int> space = yy => first ? yy : yy + gap;
            Action flush = null;
            int used;
            flush = delegate
            {
                if (para.Length == 0) return;
                y = space(y); first = false;
                y += Inline(outp, para.ToString().Trim(), x, y, width, body, bold, Ink, true, out used);
                para.Length = 0;
            };
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match fm = FenceOpen.Match(line);
                if (fm.Success)
                {
                    flush();
                    var code = new List<string>();
                    i++;
                    while (i < lines.Length && !Regex.IsMatch(lines[i], @"^\s*```\s*$")) { code.Add(lines[i]); i++; }
                    while (code.Count > 0 && code[code.Count - 1].Trim().Length == 0) code.RemoveAt(code.Count - 1);
                    string lang = fm.Groups[1].Value.ToLowerInvariant();
                    int start = fm.Groups[2].Success ? int.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                    var marks = Marked(fm.Groups[3].Value);
                    y = space(y); first = false;
                    if (lang == "diff" || lang == "fix") y = Diff(outp, code, x, y, width);
                    else if (lang == "chart" || lang == "bars") y = Chart(outp, code, x, y, width);
                    else y = Code(outp, code, lang, start, marks, fm.Groups[2].Success, x, y, width);
                    continue;
                }
                if (line.Trim().Length == 0) { flush(); continue; }
                if (line.TrimStart().StartsWith("|") && i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith("|"))
                {
                    flush();
                    var rows = new List<string>();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith("|")) { rows.Add(lines[i]); i++; }
                    i--;
                    y = space(y); first = false;
                    y = Table(outp, rows, x, y, width);
                    continue;
                }
                if (line.TrimStart().StartsWith(">"))
                {
                    flush();
                    var tip = new StringBuilder();
                    while (i < lines.Length && lines[i].TrimStart().StartsWith(">")) { tip.Append(lines[i].TrimStart().Substring(1).Trim()).Append(' '); i++; }
                    i--;
                    y = space(y); first = false;
                    y = Callout(outp, tip.ToString().Trim(), x, y, width);
                    continue;
                }
                Match hm = Heading.Match(line);
                if (hm.Success)
                {
                    flush();
                    y = space(y); first = false;
                    y += Inline(outp, hm.Groups[1].Value, x, y, width, head, head, Ink, true, out used);
                    continue;
                }
                Match nm = Numbered.Match(line), bm = Bulleted.Match(line);
                if (nm.Success || bm.Success)
                {
                    flush();
                    y = first ? y : y + S(4); first = false;
                    int indent = S(26);
                    int ty = y;
                    string itemText = nm.Success ? nm.Groups[2].Value : bm.Groups[1].Value;
                    // a wrapped item continues on the next lines that are indented
                    while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && lines[i + 1].Trim().Length > 0 && !Numbered.IsMatch(lines[i + 1]) && !Bulleted.IsMatch(lines[i + 1]))
                        itemText += " " + lines[++i].Trim();
                    int h = Inline(outp, itemText, x + indent, ty, width - indent, body, bold, Ink, true, out used);
                    int lineH = LineH(body);
                    if (nm.Success)
                    {
                        int d = S(19);
                        var r = new Rectangle(x + S(2), ty + (lineH - d) / 2, d, d);
                        outp.Add(new Piece { Kind = PK.Dot, R = r, Back = Warm });
                        outp.Add(new Piece { Kind = PK.Text, R = r, Text = nm.Groups[1].Value, Font = smallBold, Fore = Color.White });
                    }
                    else
                    {
                        int d = S(6);
                        outp.Add(new Piece { Kind = PK.Dot, R = new Rectangle(x + S(8), ty + (lineH - d) / 2, d, d), Back = Warm });
                    }
                    y += Math.Max(h, lineH);
                    continue;
                }
                para.Append(line.Trim()).Append(' ');
            }
            flush();
            return y;
        }

        static HashSet<int> Marked(string spec)
        {
            var set = new HashSet<int>();
            foreach (string part in (spec ?? "").Split(','))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int dash = p.IndexOf('-');
                int a, b;
                if (dash > 0 && int.TryParse(p.Substring(0, dash), out a) && int.TryParse(p.Substring(dash + 1), out b))
                    for (int n = a; n <= b && n - a < 200; n++) set.Add(n);
                else if (int.TryParse(p, out a)) set.Add(a);
            }
            return set;
        }

        int LineH(Font f) { return (int)Math.Ceiling(f.GetHeight() * 1.32); }

        // ------------------------------------------------------------------ inline text: bold, `code`, keys, colours, links

        enum Sty { Plain, Bold, Code, Key, Link }

        sealed class Span { public string Text; public Sty Style; public string Url; public Color? Swatch; }

        static readonly Regex InlineRx = new Regex(@"\*\*(.+?)\*\*|`([^`]+)`|(https?://[^\s)\]]+)|\[([^\]]+)\]\((https?://[^)\s]+)\)");
        static readonly Regex KeyRx = new Regex(@"^(?:(?:ctrl|alt|shift|win|cmd|super|fn)\s*\+\s*)*(?:ctrl|alt|shift|win|cmd|tab|enter|return|esc|escape|space|backspace|delete|del|home|end|pgup|pgdn|up|down|left|right|f\d{1,2}|[a-z0-9]|\W)$", RegexOptions.IgnoreCase);

        static List<Span> Spans(string text)
        {
            var list = new List<Span>();
            int at = 0;
            foreach (Match m in InlineRx.Matches(text))
            {
                if (m.Index > at) list.Add(new Span { Text = text.Substring(at, m.Index - at), Style = Sty.Plain });
                if (m.Groups[1].Success) list.Add(new Span { Text = m.Groups[1].Value, Style = Sty.Bold });
                else if (m.Groups[2].Success)
                {
                    string c = m.Groups[2].Value;
                    bool combo = c.Contains("+") && c.Trim().Length > 1;
                    if (combo && KeyRx.IsMatch(c.Trim())) list.Add(new Span { Text = c.Trim(), Style = Sty.Key });
                    else if (!combo && Regex.IsMatch(c.Trim(), @"^(ctrl|alt|shift|win|tab|enter|esc|escape|f\d{1,2})$", RegexOptions.IgnoreCase)) list.Add(new Span { Text = c.Trim(), Style = Sty.Key });
                    else list.Add(new Span { Text = c, Style = Sty.Code, Swatch = ColorIn(c) });
                }
                else if (m.Groups[3].Success) list.Add(new Span { Text = m.Groups[3].Value, Style = Sty.Link, Url = m.Groups[3].Value });
                else list.Add(new Span { Text = m.Groups[4].Value, Style = Sty.Link, Url = m.Groups[5].Value });
                at = m.Index + m.Length;
            }
            if (at < text.Length) list.Add(new Span { Text = text.Substring(at), Style = Sty.Plain });
            return list;
        }

        static readonly string[] NamedColors = { "red", "blue", "green", "yellow", "orange", "purple", "pink", "black", "white", "gray", "grey", "teal", "navy", "brown", "gold", "cyan", "magenta", "lime", "maroon", "coral", "salmon", "tomato", "crimson", "violet", "indigo", "silver" };

        /// <summary>A colour a code chip is about (#hex, rgb(), hsl(), a css colour name or "color: x"), for its swatch.</summary>
        static Color? ColorIn(string code)
        {
            string c = code.Trim();
            Match hx = Regex.Match(c, @"#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})\b");
            try
            {
                if (hx.Success) return ColorTranslator.FromHtml(hx.Value);
                Match rgb = Regex.Match(c, @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})");
                if (rgb.Success) return Color.FromArgb(Math.Min(255, int.Parse(rgb.Groups[1].Value)), Math.Min(255, int.Parse(rgb.Groups[2].Value)), Math.Min(255, int.Parse(rgb.Groups[3].Value)));
                Match named = Regex.Match(c, @"^(?:(?:background-)?colou?r\s*:\s*)?([a-z]+);?$", RegexOptions.IgnoreCase);
                if (named.Success && NamedColors.Contains(named.Groups[1].Value.ToLowerInvariant()))
                {
                    Color k = Color.FromName(named.Groups[1].Value);
                    if (k.A > 0) return k;
                }
            }
            catch { }
            return null;
        }

        static readonly StringFormat Typo = MakeTypo();
        static readonly Graphics measurer = MakeMeasurer();

        static StringFormat MakeTypo()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        static Graphics MakeMeasurer()
        {
            Graphics g = Graphics.FromImage(new Bitmap(1, 1));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            return g;
        }

        int W(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string key = f.Name + f.Size.ToString(CultureInfo.InvariantCulture) + (int)f.Style + "|" + s;
            int w;
            if (widths.TryGetValue(key, out w)) return w;
            // measured the same way it's drawn (GDI+, typographic: no padding around the ink)
            w = (int)Math.Ceiling(measurer.MeasureString(s, f, PointF.Empty, Typo).Width);
            if (widths.Count > 20000) widths.Clear();
            widths[key] = w;
            return w;
        }

        /// <summary>Lays out one paragraph, wrapping words. Returns its height; used = the widest line.</summary>
        int Inline(List<Piece> outp, string text, int x0, int y0, int width, Font plain, Font strong, Color color, bool rich, out int used)
        {
            List<Span> spans = rich ? Spans(text) : new List<Span> { new Span { Text = text, Style = Sty.Plain } };
            int lineH = LineH(plain), x = x0, y = y0, space = W(" ", plain), chipPad = S(5);
            used = 0;
            Piece lastChip = null;
            int lastChipLine = -1;
            foreach (Span sp in spans)
            {
                Font f = sp.Style == Sty.Bold ? strong : sp.Style == Sty.Code || sp.Style == Sty.Key ? monoSmall : plain;
                if (sp.Style == Sty.Key)
                {
                    // each key its own cap: Ctrl + S
                    string[] keys = Regex.Split(sp.Text, @"\s*\+\s*").Where(k => k.Length > 0).ToArray();
                    if (sp.Text.Trim() == "+") keys = new[] { "+" };
                    int total = 0;
                    foreach (string k in keys) total += W(KeyLabel(k), smallBold) + S(14) + S(14);
                    if (x + total > x0 + width && x > x0) { x = x0; y += lineH; }
                    for (int ki = 0; ki < keys.Length; ki++)
                    {
                        string label = KeyLabel(keys[ki]);
                        int kw = W(label, smallBold) + S(14), kh = smallBold.Height + S(6);
                        var r = new Rectangle(x, y + (lineH - kh) / 2, kw, kh);
                        outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(r.X, r.Y + S(2), r.Width, r.Height), Back = SandLine, Radius = S(5) });
                        outp.Add(new Piece { Kind = PK.Box, R = r, Back = Color.White, Border = SandLine, Radius = S(5) });
                        outp.Add(new Piece { Kind = PK.Text, R = r, Text = label, Font = smallBold, Fore = Ink });
                        x += kw;
                        if (ki < keys.Length - 1)
                        {
                            outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, y, S(14), lineH), Text = "+", Font = small, Fore = Muted });
                            x += S(14);
                        }
                    }
                    x += space;
                    used = Math.Max(used, x - x0);
                    lastChip = null;
                    continue;
                }
                bool chip = sp.Style == Sty.Code;
                if (chip && sp.Swatch.HasValue)
                {
                    int sw = S(12);
                    if (x + sw + S(4) > x0 + width && x > x0) { x = x0; y += lineH; }
                    outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y + (lineH - sw) / 2, sw, sw), Back = sp.Swatch.Value, Border = Color.FromArgb(60, 0, 0, 0), Radius = S(3) });
                    x += sw + S(4);
                }
                if (chip)
                {
                    // a chip that fits on a line stays in one piece (moving to the next line if it must)
                    string whole = sp.Text.Trim();
                    int cw = W(whole, f) + chipPad * 2;
                    if (whole.Length > 0 && cw <= width)
                    {
                        if (x + cw > x0 + width && x > x0) { x = x0; y += lineH; }
                        lastChip = null;
                        Place(outp, whole, f, sp, true, ref x, y, lineH, ref lastChip, ref lastChipLine, color, chipPad);
                        used = Math.Max(used, x - x0);
                        lastChip = null;
                        continue;
                    }
                }
                // words, keeping the spaces that were there
                foreach (Match wm in Regex.Matches(sp.Text, @"\S+\s*|\s+"))
                {
                    string word = wm.Value;
                    string bare = word.TrimEnd();
                    bool trailing = word.Length > bare.Length;
                    if (bare.Length == 0) { if (x > x0) x += space; continue; }
                    int ww = W(bare, f) + (chip ? chipPad * 2 : 0);
                    if (x + ww > x0 + width && x > x0) { x = x0; y += lineH; }
                    // a word longer than the line: cut it
                    while (ww > width && bare.Length > 1)
                    {
                        int n = bare.Length;
                        while (n > 1 && W(bare.Substring(0, n), f) + (chip ? chipPad * 2 : 0) > x0 + width - x) n--;
                        Place(outp, bare.Substring(0, n), f, sp, chip, ref x, y, lineH, ref lastChip, ref lastChipLine, color, chipPad);
                        used = Math.Max(used, x - x0);
                        bare = bare.Substring(n);
                        x = x0; y += lineH;
                        ww = W(bare, f) + (chip ? chipPad * 2 : 0);
                    }
                    Place(outp, bare, f, sp, chip, ref x, y, lineH, ref lastChip, ref lastChipLine, color, chipPad);
                    used = Math.Max(used, x - x0);
                    if (trailing) x += chip ? W(" ", f) : space;
                }
                if (!chip) lastChip = null;
            }
            return y - y0 + lineH;
        }

        void Place(List<Piece> outp, string word, Font f, Span sp, bool chip, ref int x, int y, int lineH, ref Piece lastChip, ref int lastChipLine, Color color, int chipPad)
        {
            int w = W(word, f);
            if (chip)
            {
                int ch = f.Height + S(4);
                var r = new Rectangle(x, y + (lineH - ch) / 2, w + chipPad * 2, ch);
                // the next word of the same chip on the same line: grow the chip instead of starting another
                if (lastChip != null && lastChipLine == y && lastChip.R.Right >= x - W(" ", f) - chipPad * 2 - 2) lastChip.R.Width = r.Right - lastChip.R.X;
                else { lastChip = new Piece { Kind = PK.Box, R = r, Back = ChipBack, Radius = S(5) }; outp.Add(lastChip); lastChipLine = y; }
                outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + chipPad, y, w + 2, lineH), Text = word, Font = f, Fore = ChipInk });
                x += w + chipPad * 2;
                return;
            }
            Color c = sp.Style == Sty.Link ? Warm : color;
            outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x, y, w + 2, lineH), Text = word, Font = f, Fore = c, Link = sp.Url, Underline = sp.Style == Sty.Link });
            x += w;
        }

        static string KeyLabel(string k)
        {
            string t = k.Trim();
            string lower = t.ToLowerInvariant();
            if (lower == "ctrl" || lower == "alt" || lower == "shift" || lower == "win" || lower == "tab" || lower == "esc" || lower == "enter" || lower == "space") return char.ToUpper(t[0]) + lower.Substring(1);
            return t.Length == 1 ? t.ToUpperInvariant() : t;
        }

        // ------------------------------------------------------------------ code card

        static readonly HashSet<string> Keywords = new HashSet<string>(("abstract as async await bool boolean break case catch char class const continue def default del delete do double elif else enum " +
            "except export extends false final finally float fn for foreach from func function get go if impl import in int interface is lambda let long match " +
            "namespace new nil none null object of package pass private protected pub public raise return self set static string struct super switch this " +
            "throw throws true try type typeof using var void while with yield select insert update where join order group by values into create table " +
            "echo then fi done esac local param foreach-object").Split(' '));

        int Code(List<Piece> outp, List<string> lines, string lang, int start, HashSet<int> marks, bool numbered, int x, int y, int width)
        {
            int headH = S(26), padX = S(10), padY = S(8);
            int lineH = (int)Math.Ceiling(mono.GetHeight() * 1.35);
            int charW = Math.Max(1, W("MMMMMMMMMM", mono) / 10);
            int lastNo = start + Math.Max(0, lines.Count - 1);
            bool showNo = numbered || lines.Count > 3 || marks.Count > 0;
            int gutter = showNo ? W(lastNo.ToString(CultureInfo.InvariantCulture), monoSmall) + S(16) : 0;
            int textX = x + padX + gutter;
            int cols = Math.Max(8, (x + width - padX - textX) / charW);
            string all = string.Join("\n", lines.ToArray());

            var rows = new List<Piece>();
            int ry = y + headH + padY;
            var state = new SynState();
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Replace("\t", "    ");
                Color[] colors = Syntax(line, lang, state);
                int no = start + i;
                bool marked = marks.Contains(no);
                List<int[]> parts = Wrap(line, cols);
                int chunks = parts.Count;
                if (marked)
                {
                    rows.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x, ry - S(1), width, chunks * lineH + S(2)), Back = CodeMark });
                    rows.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x, ry - S(1), S(3), chunks * lineH + S(2)), Back = SynKeyword });
                }
                if (showNo)
                    rows.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + padX, ry, gutter - S(10), lineH), Text = no.ToString(CultureInfo.InvariantCulture), Font = monoSmall, Fore = marked ? SynKeyword : CodeMuted, Radius = -1 });
                for (int c = 0; c < chunks; c++)
                {
                    int from = parts[c][0], to = parts[c][1];
                    int cx = textX + (c > 0 ? charW * 2 : 0);
                    // runs of one colour
                    int runStart = from;
                    for (int k = from; k <= to; k++)
                    {
                        if (k < to && colors[k] == colors[runStart]) continue;
                        if (k > runStart)
                        {
                            string run = line.Substring(runStart, k - runStart);
                            rows.Add(new Piece { Kind = PK.Text, R = new Rectangle(cx + (runStart - from) * charW, ry, (k - runStart) * charW + 2, lineH), Text = run, Font = mono, Fore = colors[runStart] });
                        }
                        runStart = k;
                    }
                    ry += lineH;
                }
            }
            int h = ry - y + padY;
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, h), Back = CodeBack, Radius = S(10) });
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, headH + S(10)), Back = CodeHead, Radius = S(10) });
            outp.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x, y + headH, width, S(10)), Back = CodeBack });
            string title = LangName(lang) + (numbered ? "  ·  from line " + start : "");
            outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + padX, y, width - S(80), headH), Text = title, Font = small, Fore = CodeMuted });
            string copyLabel = "copy";
            int cw = W(copyLabel, smallBold) + S(16);
            outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + width - cw - S(4), y, cw, headH), Text = copyLabel, Font = smallBold, Fore = SynKeyword, Copy = all });
            outp.AddRange(rows);
            return y + h;
        }

        /// <summary>Where a long code line wraps: after a space or a bracket/semicolon near the edge (never mid-word
        /// unless a word is longer than the line). Continuation lines are 2 characters in.</summary>
        static List<int[]> Wrap(string line, int cols)
        {
            var parts = new List<int[]>();
            int from = 0;
            while (true)
            {
                int room = parts.Count == 0 ? cols : Math.Max(4, cols - 2);
                if (line.Length - from <= room) { parts.Add(new[] { from, line.Length }); break; }
                int cut = from + room;
                for (int k = from + room - 1; k > from + room / 2; k--)
                {
                    char ch = line[k];
                    if (ch == ' ' || ch == ';' || ch == ',' || ch == '{' || ch == '}' || ch == '(' || ch == '>' || ch == '=') { cut = k + 1; break; }
                }
                parts.Add(new[] { from, cut });
                from = cut;
            }
            return parts;
        }

        static string LangName(string lang)
        {
            switch (lang)
            {
                case "": return "code";
                case "js": case "javascript": return "JavaScript";
                case "ts": case "typescript": return "TypeScript";
                case "py": case "python": return "Python";
                case "cs": case "csharp": case "c#": return "C#";
                case "cpp": case "c++": return "C++";
                case "html": return "HTML";
                case "css": return "CSS";
                case "json": return "JSON";
                case "sh": case "bash": case "shell": return "terminal";
                case "ps": case "ps1": case "powershell": return "PowerShell";
                case "sql": return "SQL";
                default: return lang;
            }
        }

        sealed class SynState { public bool InBlockComment, InHtmlComment, InCssBlock; }

        /// <summary>A colour per character, by a small tokenizer that knows comments, strings, numbers, keywords,
        /// html tags and attributes, and css selectors and properties. Good enough to read, never authoritative.</summary>
        static Color[] Syntax(string line, string lang, SynState st)
        {
            var c = new Color[line.Length];
            for (int i = 0; i < c.Length; i++) c[i] = CodeInk;
            bool hashComments = lang == "py" || lang == "python" || lang == "sh" || lang == "bash" || lang == "shell" || lang == "ps" || lang == "ps1" || lang == "powershell" || lang == "yaml" || lang == "yml" || lang == "rb" || lang == "ruby" || lang == "toml";
            bool markup = lang == "html" || lang == "xml" || lang == "svg" || lang == "vue";
            bool css = lang == "css" || lang == "scss";
            int n = line.Length, p = 0;
            bool inTag = false;
            while (p < n)
            {
                char ch = line[p];
                if (st.InBlockComment)
                {
                    int end = line.IndexOf("*/", p, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + 2;
                    for (int k = p; k < stop; k++) c[k] = SynComment;
                    p = stop; if (end >= 0) st.InBlockComment = false; continue;
                }
                if (st.InHtmlComment)
                {
                    int end = line.IndexOf("-->", p, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + 3;
                    for (int k = p; k < stop; k++) c[k] = SynComment;
                    p = stop; if (end >= 0) st.InHtmlComment = false; continue;
                }
                if (ch == '/' && p + 1 < n && line[p + 1] == '*') { st.InBlockComment = true; continue; }
                if (markup && string.CompareOrdinal(line, p, "<!--", 0, 4) == 0) { st.InHtmlComment = true; continue; }
                if (!markup && !css && ch == '/' && p + 1 < n && line[p + 1] == '/') { for (int k = p; k < n; k++) c[k] = SynComment; break; }
                if (hashComments && ch == '#') { for (int k = p; k < n; k++) c[k] = SynComment; break; }
                if (lang == "sql" && ch == '-' && p + 1 < n && line[p + 1] == '-') { for (int k = p; k < n; k++) c[k] = SynComment; break; }
                if (ch == '"' || ch == '\'' || ch == '`')
                {
                    int k = p + 1;
                    while (k < n && line[k] != ch) { if (line[k] == '\\') k++; k++; }
                    int stop = Math.Min(n, k + 1);
                    for (int q = p; q < stop; q++) c[q] = SynString;
                    p = stop; continue;
                }
                if (markup && ch == '<')
                {
                    int k = p + 1;
                    if (k < n && line[k] == '/') k++;
                    while (k < n && (char.IsLetterOrDigit(line[k]) || line[k] == '-')) k++;
                    for (int q = p; q < k; q++) c[q] = SynTag;
                    inTag = true; p = k; continue;
                }
                if (markup && ch == '>' && inTag) { c[p] = SynTag; inTag = false; p++; continue; }
                if (markup && inTag && char.IsLetter(ch))
                {
                    int k = p;
                    while (k < n && (char.IsLetterOrDigit(line[k]) || line[k] == '-' || line[k] == ':')) k++;
                    for (int q = p; q < k; q++) c[q] = SynAttr;
                    p = k; continue;
                }
                bool cssHere = css || (markup && !inTag && (line.Contains("{") || st.InCssBlock));
                if (cssHere)
                {
                    if (ch == '{') { st.InCssBlock = true; p++; continue; }
                    if (ch == '}') { st.InCssBlock = false; p++; continue; }
                    if (char.IsLetter(ch) || ch == '-' || ch == '.' || ch == '#')
                    {
                        int k = p;
                        while (k < n && (char.IsLetterOrDigit(line[k]) || line[k] == '-' || line[k] == '.' || line[k] == '#' || line[k] == '_')) k++;
                        int after = k;
                        while (after < n && line[after] == ' ') after++;
                        Color col = st.InCssBlock ? (after < n && line[after] == ':' ? SynAttr : CodeInk) : SynTag;
                        for (int q = p; q < k; q++) c[q] = col;
                        p = k; continue;
                    }
                }
                if (char.IsDigit(ch) && (p == 0 || !char.IsLetter(line[p - 1])))
                {
                    int k = p;
                    while (k < n && (char.IsLetterOrDigit(line[k]) || line[k] == '.' || line[k] == '%')) k++;
                    for (int q = p; q < k; q++) c[q] = SynNumber;
                    p = k; continue;
                }
                if (char.IsLetter(ch) || ch == '_' || ch == '$')
                {
                    int k = p;
                    while (k < n && (char.IsLetterOrDigit(line[k]) || line[k] == '_' || line[k] == '$' || line[k] == '-' && lang.StartsWith("ps"))) k++;
                    string word = line.Substring(p, k - p);
                    if (!markup && Keywords.Contains(word.ToLowerInvariant()) && (lang == "sql" || word == word.ToLowerInvariant() || lang.StartsWith("ps")))
                        for (int q = p; q < k; q++) c[q] = SynKeyword;
                    else if (k < n && line[k] == '(') for (int q = p; q < k; q++) c[q] = SynTag;
                    p = k; continue;
                }
                p++;
            }
            return c;
        }

        // ------------------------------------------------------------------ fix card (diff)

        int Diff(List<Piece> outp, List<string> lines, int x, int y, int width)
        {
            int lineH = (int)Math.Ceiling(mono.GetHeight() * 1.4), padX = S(10);
            int charW = Math.Max(1, W("MMMMMMMMMM", mono) / 10);
            string title = null;
            if (lines.Count > 0 && lines[0].TrimStart().StartsWith("#")) { title = lines[0].TrimStart().TrimStart('#').Trim(); lines = lines.Skip(1).ToList(); }
            int headH = title != null ? S(28) : 0;
            int gutter = S(22);
            int cols = Math.Max(8, (width - padX * 2 - gutter) / charW);
            var rows = new List<Piece>();
            int ry = y + headH + S(6);
            var copy = new StringBuilder();
            foreach (string raw in lines)
            {
                string line = raw.Replace("\t", "    ");
                char sign = line.Length > 0 ? line[0] : ' ';
                bool add = sign == '+', del = sign == '-';
                string bodyText = add || del || sign == ' ' ? (line.Length > 1 ? line.Substring(1) : "") : line;
                if (bodyText.StartsWith(" ") && (add || del)) bodyText = bodyText.Substring(1);
                if (!del) copy.Append(bodyText).Append('\n');
                List<int[]> parts = Wrap(bodyText, cols);
                int chunks = parts.Count;
                if (add || del) rows.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x + S(1), ry, width - S(2), chunks * lineH), Back = add ? AddBack : DelBack });
                rows.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + padX, ry, gutter, lineH), Text = add ? "+" : del ? "−" : "", Font = mono, Fore = add ? AddInk : DelInk });
                for (int ci = 0; ci < chunks; ci++)
                {
                    string part = bodyText.Substring(parts[ci][0], parts[ci][1] - parts[ci][0]);
                    rows.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + padX + gutter + (ci > 0 ? charW * 2 : 0), ry, width - padX * 2 - gutter, lineH), Text = part, Font = mono, Fore = add ? AddInk : del ? DelInk : Ink });
                    ry += lineH;
                }
            }
            int h = ry - y + S(6);
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, h), Back = Color.White, Border = SandLine, Radius = S(10) });
            if (title != null)
            {
                outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, headH + S(8)), Back = Sand, Border = SandLine, Radius = S(10) });
                outp.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x + S(1), y + headH, width - S(2), S(8)), Back = Color.White });
                outp.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x + S(1), y + headH - S(1), width - S(2), S(1)), Back = SandLine });
                outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + padX, y, width - S(90), headH), Text = "fix  ·  " + title, Font = smallBold, Fore = Ink });
                int cw = W("copy fixed", smallBold) + S(16);
                outp.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + width - cw - S(4), y, cw, headH), Text = "copy fixed", Font = smallBold, Fore = Warm, Copy = copy.ToString().TrimEnd('\n') });
            }
            outp.AddRange(rows);
            return y + h;
        }

        // ------------------------------------------------------------------ little bar chart

        int Chart(List<Piece> outp, List<string> lines, int x, int y, int width)
        {
            string title = null;
            var rows = new List<Tuple<string, double, string>>();
            foreach (string raw in lines)
            {
                string l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("#")) { title = l.TrimStart('#').Trim(); continue; }
                Match m = Regex.Match(l, @"^(.+?)\s*[:=|]\s*(-?[\d,]*\.?\d+)\s*(.*)$");
                double v;
                if (m.Success && double.TryParse(m.Groups[2].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    rows.Add(Tuple.Create(m.Groups[1].Value.Trim(), v, m.Groups[3].Value.Trim()));
            }
            if (rows.Count == 0) return y;
            int pad = S(12), barH = S(14), rowH = S(26);
            int labelW = Math.Min((int)(width * 0.38), rows.Max(r => W(r.Item1, small)) + S(8));
            double max = Math.Max(1e-9, rows.Max(r => Math.Abs(r.Item2)));
            int ty = y + pad;
            var inner = new List<Piece>();
            if (title != null) { inner.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + pad, ty, width - pad * 2, small.Height + S(4)), Text = title, Font = smallBold, Fore = Ink }); ty += small.Height + S(8); }
            int trackX = x + pad + labelW, trackW = width - pad * 2 - labelW - S(70);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int by = ty + (rowH - barH) / 2;
                inner.Add(new Piece { Kind = PK.Text, R = new Rectangle(x + pad, ty, labelW - S(6), rowH), Text = r.Item1, Font = small, Fore = Ink, Radius = -2 });
                inner.Add(new Piece { Kind = PK.Box, R = new Rectangle(trackX, by, trackW, barH), Back = Sand, Radius = barH / 2 });
                int bw = Math.Max(barH, (int)(trackW * Math.Abs(r.Item2) / max));
                bool top = Math.Abs(r.Item2) >= max - 1e-9;
                inner.Add(new Piece { Kind = PK.Box, R = new Rectangle(trackX, by, bw, barH), Back = top ? Warm : Color.FromArgb(214, 150, 108), Radius = barH / 2 });
                string val = r.Item2.ToString(r.Item2 == Math.Floor(r.Item2) ? "#,0" : "#,0.##", CultureInfo.InvariantCulture) + (r.Item3.Length > 0 ? " " + r.Item3 : "");
                inner.Add(new Piece { Kind = PK.Text, R = new Rectangle(trackX + trackW + S(8), ty, S(70), rowH), Text = val, Font = top ? smallBold : small, Fore = top ? Warm : Muted });
                ty += rowH;
            }
            int h = ty - y + pad - S(4);
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, h), Back = Color.White, Border = SandLine, Radius = S(10) });
            outp.AddRange(inner);
            return y + h;
        }

        // ------------------------------------------------------------------ table and tip

        int Table(List<Piece> outp, List<string> rows, int x, int y, int width)
        {
            var cells = new List<string[]>();
            foreach (string r in rows)
            {
                string t = r.Trim().Trim('|');
                if (Regex.IsMatch(t, @"^[\s:\-|]+$")) continue;   // the |---| line
                cells.Add(t.Split('|').Select(s => s.Trim()).ToArray());
            }
            if (cells.Count == 0) return y;
            int cols = cells.Max(c => c.Length);
            int pad = S(8);
            var natural = new int[cols];
            for (int r0 = 0; r0 < cells.Count; r0++)
                for (int c = 0; c < cells[r0].Length; c++)
                {
                    int used;   // laid out on one endless line: its real width, chips and all
                    Inline(new List<Piece>(), cells[r0][c], 0, 0, 100000, r0 == 0 ? bold : body, bold, Ink, true, out used);
                    natural[c] = Math.Max(natural[c], used + pad * 2 + S(2));
                }
            int total = natural.Sum();
            var colW = new int[cols];
            for (int c = 0; c < cols; c++) colW[c] = total <= width ? natural[c] : Math.Max(S(56), (int)(width * (double)natural[c] / total));
            int tableW = Math.Min(width, colW.Sum());
            var inner = new List<Piece>();
            int ry = y;
            for (int r = 0; r < cells.Count; r++)
            {
                int cx = x, rowH = 0;
                var rowPieces = new List<Piece>();
                for (int c = 0; c < cols; c++)
                {
                    string text = c < cells[r].Length ? cells[r][c] : "";
                    int used;
                    int h = Inline(rowPieces, text, cx + pad, ry + S(5), colW[c] - pad * 2, r == 0 ? bold : body, bold, Ink, true, out used);
                    rowH = Math.Max(rowH, h + S(10));
                    cx += colW[c];
                }
                if (r == 0) inner.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x + S(1), ry + S(1), tableW - S(2), rowH - S(1)), Back = Sand });
                else inner.Add(new Piece { Kind = PK.Bar, R = new Rectangle(x + S(1), ry, tableW - S(2), S(1)), Back = SandLine });
                inner.AddRange(rowPieces);
                ry += rowH;
            }
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, tableW, ry - y), Back = Color.White, Border = SandLine, Radius = S(8) });
            outp.AddRange(inner);
            return ry;
        }

        static string StripInline(string s) { return Regex.Replace(s ?? "", @"\*\*|`", ""); }

        int Callout(List<Piece> outp, string text, int x, int y, int width)
        {
            int pad = S(10), used;
            var inner = new List<Piece>();
            int h = Inline(inner, text, x + pad + S(6), y + pad, width - pad * 2 - S(6), body, bold, Ink, true, out used);
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, width, h + pad * 2), Back = Color.FromArgb(252, 238, 222), Radius = S(8) });
            outp.Add(new Piece { Kind = PK.Box, R = new Rectangle(x, y, S(4), h + pad * 2), Back = Warm, Radius = S(2) });
            outp.AddRange(inner);
            return y + h + pad * 2;
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs pe)
        {
            Graphics g = pe.Graphics;
            g.Clear(Cream);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int y = S(8) - scrollY, h = ClientSize.Height;
            foreach (Entry e in entries)
            {
                if (y + e.Height >= 0 && y <= h) PaintEntry(g, e, Pad, y);
                y += e.Height + S(14);
            }
            // a slim scrollbar when there's more than fits
            if (contentH > h)
            {
                Rectangle t = Thumb();
                using (var br = new SolidBrush(Color.FromArgb(dragThumb ? 150 : 90, Muted))) FillRound(g, br, t, t.Width / 2);
            }
        }

        void PaintEntry(Graphics g, Entry e, int ox, int oy)
        {
            foreach (Piece p in e.Pieces)
            {
                var r = new Rectangle(p.R.X + ox, p.R.Y + oy, p.R.Width, p.R.Height);
                switch (p.Kind)
                {
                    case PK.Bar:
                        using (var br = new SolidBrush(p.Back)) g.FillRectangle(br, r);
                        break;
                    case PK.Box:
                        using (var br = new SolidBrush(p.Back)) FillRound(g, br, r, p.Radius);
                        if (p.Border.A > 0) using (var pen = new Pen(p.Border, Math.Max(1, S(1)))) DrawRound(g, pen, r, p.Radius);
                        break;
                    case PK.Dot:
                        using (var br = new SolidBrush(p.Back)) g.FillEllipse(br, r);
                        break;
                    case PK.Text:
                        {
                            string text = p.Text;
                            Color fore = p.Fore;
                            if (p.Copy != null && copied == p) { text = "copied ✓"; fore = p.Fore; }
                            bool center = p.Font == smallBold && p.Fore == Color.White || p.Copy != null || p.Font == smallBold && p.R.Width < S(60) && p.Fore == Ink;   // step numbers, copy, key caps
                            float tw = W(text, p.Font);
                            float ty = r.Y + (r.Height - p.Font.GetHeight(g)) / 2f;
                            float tx = center ? r.X + (r.Width - tw) / 2f : p.Radius == -1 ? r.Right - tw : r.X;   // -1: line numbers, right aligned
                            using (var br = new SolidBrush(fore))
                            {
                                if (p.Radius == -2 && tw > r.Width)   // a chart label too long for its column
                                    using (var sf = (StringFormat)Typo.Clone()) { sf.Trimming = StringTrimming.EllipsisCharacter; g.DrawString(text, p.Font, br, new RectangleF(tx, ty, r.Width, p.Font.GetHeight(g) + 2), sf); }
                                else g.DrawString(text, p.Font, br, tx, ty, Typo);
                            }
                            if (p.Underline)
                                using (var pen = new Pen(Color.FromArgb(120, fore), 1)) g.DrawLine(pen, tx, r.Y + r.Height - S(5), tx + tw, r.Y + r.Height - S(5));
                            break;
                        }
                }
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            if (rad <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void FillRound(Graphics g, Brush b, Rectangle r, int rad) { using (GraphicsPath p = Round(r, rad)) g.FillPath(b, p); }
        static void DrawRound(Graphics g, Pen pen, Rectangle r, int rad) { using (GraphicsPath p = Round(r, rad)) g.DrawPath(pen, p); }

        // ------------------------------------------------------------------ scrolling and clicks

        Rectangle Thumb()
        {
            int h = ClientSize.Height;
            int th = Math.Max(S(30), (int)(h * (double)h / Math.Max(1, contentH)));
            int ty = (int)((h - th) * (scrollY / (double)Math.Max(1, contentH - h)));
            return new Rectangle(ClientSize.Width - BarW + S(1), ty, BarW - S(3), th);
        }

        void ScrollBy(int dy)
        {
            int was = scrollY;
            scrollY = Math.Max(0, Math.Min(scrollY + dy, Math.Max(0, contentH - ClientSize.Height)));
            if (scrollY != was) Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollBy(-e.Delta * S(60) / 120);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left && contentH > ClientSize.Height && e.X >= ClientSize.Width - BarW - S(2))
            {
                Rectangle t = Thumb();
                if (t.Contains(e.Location)) { dragThumb = true; dragFrom = e.Y; dragScroll = scrollY; Capture = true; Invalidate(); }
                else ScrollBy(e.Y < t.Y ? -ClientSize.Height + S(40) : ClientSize.Height - S(40));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragThumb)
            {
                int h = ClientSize.Height, th = Thumb().Height;
                double per = (contentH - h) / (double)Math.Max(1, h - th);
                scrollY = Math.Max(0, Math.Min(dragScroll + (int)((e.Y - dragFrom) * per), Math.Max(0, contentH - h)));
                Invalidate();
                return;
            }
            Entry en; Piece p = HitPiece(e.Location, out en);
            Cursor = p != null && (p.Link != null || p.Copy != null) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragThumb) { dragThumb = false; Capture = false; Invalidate(); return; }
            Entry en;
            Piece p = HitPiece(e.Location, out en);
            if (e.Button == MouseButtons.Right)
            {
                menuEntry = en;
                menu.Items[0].Enabled = en != null;
                menu.Show(this, e.Location);
                return;
            }
            if (p == null) return;
            if (p.Copy != null)
            {
                CopyText(p.Copy);
                copied = p; copiedAt = DateTime.UtcNow;
                copyTimer.Stop(); copyTimer.Start();
                Invalidate();
            }
            else if (p.Link != null && LinkClicked != null) LinkClicked(p.Link);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.PageUp) ScrollBy(-ClientSize.Height + S(40));
            else if (e.KeyCode == Keys.PageDown) ScrollBy(ClientSize.Height - S(40));
            else if (e.KeyCode == Keys.Home && e.Control) ScrollBy(-contentH);
            else if (e.KeyCode == Keys.End && e.Control) ScrollToEnd();
            else if (e.KeyCode == Keys.C && e.Control && entries.Count > 0) CopyText(entries[entries.Count - 1].Text);
        }

        Piece HitPiece(Point pt, out Entry hit)
        {
            hit = null;
            int y = S(8) - scrollY;
            foreach (Entry e in entries)
            {
                if (pt.Y >= y && pt.Y < y + e.Height + S(14))
                {
                    hit = e;
                    for (int i = e.Pieces.Count - 1; i >= 0; i--)
                    {
                        Piece p = e.Pieces[i];
                        if ((p.Link != null || p.Copy != null) && new Rectangle(p.R.X + Pad, p.R.Y + y, p.R.Width, p.R.Height).Contains(pt)) return p;
                    }
                    return null;
                }
                y += e.Height + S(14);
            }
            return null;
        }

        // ------------------------------------------------------------------ for checks

        /// <summary>Draws the whole history (not just what's scrolled into view) into a bitmap of this width.</summary>
        internal Bitmap RenderAll(int width)
        {
            Size = new Size(width, 400);
            Relayout();
            var bmp = new Bitmap(width, Math.Max(40, contentH + S(8)));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Cream);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                int y = S(8);
                foreach (Entry e in entries) { PaintEntry(g, e, Pad, y); y += e.Height + S(14); }
            }
            return bmp;
        }
    }
}
