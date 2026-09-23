using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>A 32-bit premultiplied DIB we can draw on with GDI+ and hand straight to UpdateLayeredWindow.</summary>
    public sealed class Surface : IDisposable
    {
        public readonly int Width, Height;
        public readonly IntPtr Dc;
        public readonly Bitmap Bitmap;
        public readonly Graphics G;
        readonly IntPtr hBitmap, oldObj;

        public Surface(int w, int h)
        {
            Width = Math.Max(1, w); Height = Math.Max(1, h);
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            Dc = Native.CreateCompatibleDC(screen);
            IntPtr bits;
            hBitmap = Native.CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldObj = Native.SelectObject(Dc, hBitmap);
            Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, bits);
            G = Graphics.FromImage(Bitmap);
        }

        public void Clear() { G.ResetTransform(); G.ResetClip(); G.Clear(Color.Transparent); }

        public void Dispose()
        {
            G.Dispose();
            Bitmap.Dispose();
            Native.SelectObject(Dc, oldObj);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(Dc);
        }
    }

    /// <summary>Borderless, topmost, never-activating window whose pixels (with alpha) come from a Surface.
    /// Fully transparent pixels let clicks through to whatever is underneath.</summary>
    public class LayeredWindow : Form
    {
        protected bool ClickThrough;

        public LayeredWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(1, 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                if (ClickThrough) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public void Present(Surface s, int x, int y, byte alpha)
        {
            if (!IsHandleCreated) return;
            var dst = new POINT(x, y);
            var size = new SIZE(s.Width, s.Height);
            var src = new POINT(0, 0);
            var blend = new BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = alpha, AlphaFormat = Native.AC_SRC_ALPHA };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, s.Dc, ref src, 0, ref blend, Native.ULW_ALPHA);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        public void KeepOnTop()
        {
            if (IsHandleCreated)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }
    }

    /// <summary>The speech bubble: its own click-through layered window so it can hang off the creature and stay on screen.</summary>
    public sealed class BubbleWindow : LayeredWindow
    {
        Surface surf;
        string drawnText;
        bool drawnThought, drawnTailUp;
        int drawnAlpha = -1;
        float drawnTailX;
        bool shown;
        string measuredText;
        float measuredScale;
        SizeF measuredFull;

        public BubbleWindow() { ClickThrough = true; }

        public void Update(Creature c)
        {
            if (c.BubbleText == null || c.Mode == Mode.Hidden || c.Alpha <= 0.01)
            {
                if (shown && surf != null) { Present(surf, -32000, -32000, 0); shown = false; }
                return;
            }
            float s = (float)c.S;
            double age = Clock.T - c.BubbleStart, left = c.BubbleUntil - Clock.T;
            int alpha = (int)(255 * Math.Min(1, Math.Min(age / 0.15, left / 0.3)) * c.Alpha);
            alpha = Math.Max(0, Math.Min(255, alpha));

            if (measuredText != c.BubbleText || measuredScale != s)
            {
                SizeF textSize;
                using (var tmp = Graphics.FromHwnd(IntPtr.Zero)) measuredFull = Renderer.MeasureBubble(tmp, c.BubbleText, s, out textSize);
                measuredText = c.BubbleText; measuredScale = s;
            }
            SizeF full = measuredFull;
            int margin = (int)(6 * s);
            int w = (int)Math.Ceiling(full.Width) + margin * 2, h = (int)Math.Ceiling(full.Height) + margin * 2;
            float bodyH = full.Height - 12 * s;

            double hx, hy;
            c.HeadPoint(out hx, out hy);
            var mon = c.World.MonAt(c.X, c.Y);
            Rectangle area = mon != null ? Rectangle.FromLTRB(mon.Work.Left, mon.Work.Top, mon.Work.Right, mon.Work.Bottom) : new Rectangle(0, 0, 1920, 1080);

            bool upsideDown = c.Mode == Mode.Ceiling;
            int x = (int)(hx - w / 2.0);
            int y = upsideDown ? (int)(hy + 4 * s) : (int)(hy - h + margin - 2 * s);
            bool tailUp = upsideDown;
            if (!upsideDown && y < area.Top + 2) { y = (int)(c.Y + 6 * s); tailUp = true; }
            x = Math.Max(area.Left + 2, Math.Min(area.Right - w - 2, x));
            y = Math.Max(area.Top + 2, Math.Min(area.Bottom - h - 2, y));
            float tailX = (float)(hx - x);

            if (surf == null || surf.Width != w || surf.Height != h)
            {
                if (surf != null) surf.Dispose();
                surf = new Surface(w, h);
                drawnText = null;
            }
            if (drawnText != c.BubbleText || drawnThought != c.BubbleThought || drawnAlpha != alpha || drawnTailUp != tailUp || Math.Abs(drawnTailX - tailX) > 1)
            {
                surf.Clear();
                float by = tailUp ? margin + 12 * s : margin;
                Renderer.DrawBubble(surf.G, c.BubbleText, c.BubbleThought, margin, by, full.Width, bodyH, tailX, tailUp, s, alpha);
                drawnText = c.BubbleText; drawnThought = c.BubbleThought; drawnAlpha = alpha; drawnTailUp = tailUp; drawnTailX = tailX;
            }
            Present(surf, x, y, 255);
            shown = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && surf != null) surf.Dispose();
            base.Dispose(disposing);
        }
    }
}
