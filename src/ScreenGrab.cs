// ScreenGrab.cs - screenshots of one monitor (physical pixels; the process is per-monitor-v2
// DPI aware), JPEG data URLs for the model, thumbnails, and the DPI scale at a point. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bestie
{
    internal static class ScreenGrabNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct GrabRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GrabMonitorInfo { public int cbSize; public GrabRect rcMonitor; public GrabRect rcWork; public uint dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GrabPoint { public int X, Y; }

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(GrabPoint pt, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref GrabMonitorInfo mi);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out GrabPoint p);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out GrabRect rect);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        public const int GWL_STYLE = -16;
        public const int WS_CAPTION = 0x00C00000;
    }

    internal static class ScreenGrab
    {
        static readonly uint ourPid = (uint)Process.GetCurrentProcess().Id;

        /// <summary>The foreground window, unless it belongs to this process (then Zero).</summary>
        public static IntPtr ForegroundWindowNotOurs()
        {
            try
            {
                IntPtr h = ScreenGrabNative.GetForegroundWindow();
                if (h == IntPtr.Zero) return IntPtr.Zero;
                uint pid;
                ScreenGrabNative.GetWindowThreadProcessId(h, out pid);
                return pid == ourPid ? IntPtr.Zero : h;
            }
            catch { return IntPtr.Zero; }
        }

        public static string WindowTitle(IntPtr hwnd)
        {
            try
            {
                var sb = new System.Text.StringBuilder(512);
                ScreenGrabNative.GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        /// <summary>Process name of the window's owner ("spotify", "chrome"), or "".</summary>
        public static string WindowApp(IntPtr hwnd)
        {
            try
            {
                uint pid;
                ScreenGrabNative.GetWindowThreadProcessId(hwnd, out pid);
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant();
            }
            catch { return ""; }
        }

        /// <summary>True for a window that covers its whole monitor without a caption (games, videos,
        /// presentations, the bare desktop). A normal maximized window has a caption.</summary>
        public static bool IsCaptionlessFullscreen(IntPtr hwnd)
        {
            try
            {
                ScreenGrabNative.GrabRect r;
                if (!ScreenGrabNative.GetWindowRect(hwnd, out r)) return false;
                Rectangle mon = MonitorBounds(hwnd);
                bool covers = r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
                int style = ScreenGrabNative.GetWindowLong(hwnd, ScreenGrabNative.GWL_STYLE);
                return covers && (style & ScreenGrabNative.WS_CAPTION) != ScreenGrabNative.WS_CAPTION;
            }
            catch { return false; }
        }

        /// <summary>Full bounds of the monitor holding hwnd (or the cursor if hwnd is Zero).</summary>
        public static Rectangle MonitorBounds(IntPtr hwnd)
        {
            try
            {
                IntPtr mon;
                if (hwnd != IntPtr.Zero && ScreenGrabNative.IsWindow(hwnd))
                    mon = ScreenGrabNative.MonitorFromWindow(hwnd, ScreenGrabNative.MONITOR_DEFAULTTONEAREST);
                else
                {
                    ScreenGrabNative.GrabPoint p;
                    ScreenGrabNative.GetCursorPos(out p);
                    mon = ScreenGrabNative.MonitorFromPoint(p, ScreenGrabNative.MONITOR_DEFAULTTONEAREST);
                }
                var mi = new ScreenGrabNative.GrabMonitorInfo();
                mi.cbSize = Marshal.SizeOf(typeof(ScreenGrabNative.GrabMonitorInfo));
                if (mon != IntPtr.Zero && ScreenGrabNative.GetMonitorInfo(mon, ref mi))
                    return Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            }
            catch (Exception ex) { DebugLog.Write("assistant: monitor lookup: " + ex.Message); }
            return Screen.PrimaryScreen.Bounds;
        }

        public static Bitmap Capture(Rectangle r)
        {
            var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }

        public static Bitmap CaptureMonitorOf(IntPtr hwnd) { return Capture(MonitorBounds(hwnd)); }

        /// <summary>Every monitor, left to right, each as a JPEG data URL (&lt;= maxWidth wide). Hides nothing.
        /// layout describes them, e.g. "screen 1 (primary) 1920x1080, screen 2 2560x1440".</summary>
        public static List<string> CaptureAllScreens(int maxWidth, long quality, out string layout)
        {
            var urls = new List<string>();
            var desc = new List<string>();
            Screen[] screens = Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToArray();
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle b = screens[i].Bounds;
                try
                {
                    using (Bitmap bmp = Capture(b)) urls.Add(ToJpegDataUrl(bmp, maxWidth, quality));
                    desc.Add("screen " + urls.Count + (screens[i].Primary ? " (primary)" : "") + " " + b.Width + "x" + b.Height);
                }
                catch (Exception ex) { DebugLog.Write("assistant: couldn't capture screen " + (i + 1) + ": " + ex.Message); }
            }
            layout = string.Join(", ", desc.ToArray());
            return urls;
        }

        /// <summary>Downscale to at most maxWidth and encode as a JPEG data URL.</summary>
        public static string ToJpegDataUrl(Bitmap src, int maxWidth, long quality)
        {
            Bitmap img = src;
            bool own = false;
            if (src.Width > maxWidth)
            {
                int h = Math.Max(1, (int)Math.Round(src.Height * (double)maxWidth / src.Width));
                img = Resize(src, maxWidth, h);
                own = true;
            }
            try
            {
                ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var ep = new EncoderParameters(1))
                using (var ms = new MemoryStream())
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    img.Save(ms, codec, ep);
                    return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
            finally { if (own) img.Dispose(); }
        }

        static Bitmap Resize(Image src, int w, int h)
        {
            var dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        public static Bitmap Thumbnail(Image src, int maxW, int maxH)
        {
            double k = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
            if (k > 1) k = 1;
            return Resize(src, Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
        }

        /// <summary>Monitor DPI / 96 at a physical screen point (1.0 if unknown).</summary>
        public static float ScaleAt(Point p)
        {
            try
            {
                var gp = new ScreenGrabNative.GrabPoint();
                gp.X = p.X; gp.Y = p.Y;
                IntPtr mon = ScreenGrabNative.MonitorFromPoint(gp, ScreenGrabNative.MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (mon != IntPtr.Zero && ScreenGrabNative.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx >= 48) return dx / 96f;
            }
            catch { }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }
            catch { return 1f; }
        }
    }
}
