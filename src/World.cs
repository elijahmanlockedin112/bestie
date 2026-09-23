using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bestie
{
    public class Mon
    {
        public int Index;
        public RECT Bounds, Work;
        public bool Primary;
        public double Scale;
        public bool WallLeft, WallRight;   // outer edges with no neighbouring monitor
        /// <summary>Stable-ish identity across runs: resolution + position.</summary>
        public string Key { get { return Bounds.Width + "x" + Bounds.Height + "@" + Bounds.Left + "," + Bounds.Top; } }
        public double CenterX { get { return (Work.Left + Work.Right) / 2.0; } }
    }

    public class Win
    {
        public IntPtr Hwnd;
        public RECT R;
        public string App;
        public string Title;
        public int Z;             // 0 = topmost
        public bool Maximized;
    }

    public enum SegKind { Floor, Plank, Ceiling }

    /// <summary>A horizontal surface the creature can stand on (or hang from, for ceilings).</summary>
    public class Seg
    {
        public SegKind Kind;
        public double X1, X2, Y;
        public IntPtr Hwnd;       // plank owner, zero for floors/ceilings
        public string App;        // plank owner's process name
        public int Mon;
        public int Z;             // plank's window z-order (0 = topmost)

        public double Width { get { return X2 - X1; } }
        public double Mid { get { return (X1 + X2) / 2; } }
        public bool Contains(double x, double pad) { return x >= X1 - pad && x <= X2 + pad; }
        public bool SameAs(Seg o)
        {
            if (o == null || o.Kind != Kind) return false;
            return Kind == SegKind.Plank ? o.Hwnd == Hwnd : o.Mon == Mon;
        }
        public override string ToString()
        {
            return Kind + (App != null ? "(" + App + ")" : "") + " m" + Mon + " [" + (int)X1 + ".." + (int)X2 + "] y" + (int)Y;
        }
    }

    /// <summary>A snapshot of the desktop as the creature perceives it: monitors, windows and walkable surfaces.</summary>
    public class World
    {
        public List<Mon> Mons = new List<Mon>();
        public List<Win> Wins = new List<Win>();
        public List<Seg> Segs = new List<Seg>();    // floors + planks
        public List<Seg> Ceils = new List<Seg>();
        public int FullscreenMon = -1;
        public Win Foreground;
        public double Scale = 1;

        static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32768", "tooltips_class32",
            "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow", "TopLevelWindowForOverflowXamlIsland",
            "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame", "TaskListThumbnailWnd",
            "Xaml_WindowedPopupClass", "SysShadow", "IME", "MSCTFIME UI"
        };

        static readonly Dictionary<uint, string> procNames = new Dictionary<uint, string>();

        public static World Scan(uint ownPid, double bodyHeight)
        {
            var w = new World();
            w.ScanMonitors();
            w.Scale = w.Mons.Count > 0 ? w.Mons.Max(m => m.Scale) : 1;
            w.ScanWindows(ownPid);
            w.BuildSurfaces(bodyHeight);
            return w;
        }

        void ScanMonitors()
        {
            var found = new List<Mon>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr dc, ref RECT r, IntPtr d)
            {
                var info = new MONITORINFO();
                info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO));
                if (Native.GetMonitorInfo(h, ref info))
                {
                    var m = new Mon { Bounds = info.rcMonitor, Work = info.rcWork, Primary = (info.dwFlags & 1) != 0, Scale = 1 };
                    try { uint dx, dy; if (Native.GetDpiForMonitor(h, 0, out dx, out dy) == 0) m.Scale = dx / 96.0; } catch { }
                    found.Add(m);
                }
                return true;
            }, IntPtr.Zero);
            // left-to-right so "screen one" is the leftmost one
            Mons = found.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
            for (int i = 0; i < Mons.Count; i++) Mons[i].Index = i;
            foreach (var m in Mons)
            {
                m.WallLeft = !Mons.Any(o => o != m && Math.Abs(o.Work.Right - m.Work.Left) <= 2 && Overlap(o, m) > 0.5);
                m.WallRight = !Mons.Any(o => o != m && Math.Abs(o.Work.Left - m.Work.Right) <= 2 && Overlap(o, m) > 0.5);
            }
        }

        static double Overlap(Mon a, Mon b)
        {
            int top = Math.Max(a.Work.Top, b.Work.Top), bot = Math.Min(a.Work.Bottom, b.Work.Bottom);
            return Math.Max(0, bot - top) / (double)Math.Max(1, b.Work.Height);
        }

        void ScanWindows(uint ownPid)
        {
            IntPtr fg = Native.GetForegroundWindow();
            int z = 0;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid == ownPid) return true;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
                if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) return true;
                if (Native.IsCloaked(h)) return true;
                string cls = Native.ClassOf(h);
                if (IgnoredClasses.Contains(cls)) return true;
                string title = Native.TitleOf(h);
                if (title.Length == 0) return true;
                RECT r;
                if (!Native.FrameBounds(h, out r) || r.Width < 100 || r.Height < 50) return true;

                var win = new Win { Hwnd = h, R = r, Title = title, Z = z++, Maximized = Native.IsZoomed(h) };
                win.App = AppName(pid, cls, title);
                Wins.Add(win);
                if (h == fg) Foreground = win;
                return true;
            }, IntPtr.Zero);

            if (fg != IntPtr.Zero && Foreground == null)
            {
                // fullscreen games/videos often use odd styles; check the raw foreground window too
                RECT r; string cls = Native.ClassOf(fg);
                if (!IgnoredClasses.Contains(cls) && Native.GetWindowRect(fg, out r))
                    Foreground = new Win { Hwnd = fg, R = r, Title = Native.TitleOf(fg), App = "?", Z = -1 };
            }
            // "fullscreen" = covers a whole monitor without a title bar (games, videos, presentations).
            // A maximized window can cover the monitor too when the taskbar auto-hides, but it keeps its caption.
            bool captionless = fg != IntPtr.Zero && (Native.GetWindowLong(fg, Native.GWL_STYLE) & Native.WS_CAPTION) != Native.WS_CAPTION;
            if (Foreground != null && captionless)
            {
                foreach (var m in Mons)
                {
                    RECT r = Foreground.R, b = m.Bounds;
                    if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) { FullscreenMon = m.Index; break; }
                }
            }
        }

        static string AppName(uint pid, string cls, string title)
        {
            string name;
            lock (procNames)
            {
                if (!procNames.TryGetValue(pid, out name))
                {
                    try { name = Native.ProcessNameOf(pid); } catch { name = null; }
                    if (procNames.Count > 500) procNames.Clear();
                    procNames[pid] = name;
                }
            }
            if (name == null) name = cls.ToLowerInvariant();
            // UWP apps all live inside ApplicationFrameHost; their title is the app's name
            if (name == "applicationframehost") name = "app:" + title.ToLowerInvariant();
            return name;
        }

        void BuildSurfaces(double bodyHeight)
        {
            foreach (var m in Mons)
            {
                Segs.Add(new Seg { Kind = SegKind.Floor, X1 = m.Work.Left, X2 = m.Work.Right, Y = m.Work.Bottom, Mon = m.Index, Z = int.MaxValue });
                Ceils.Add(new Seg { Kind = SegKind.Ceiling, X1 = m.Work.Left, X2 = m.Work.Right, Y = m.Work.Top, Mon = m.Index, Z = int.MaxValue });
            }

            double inset = 6 * Scale, minWidth = 44 * Scale, headroom = bodyHeight * 1.4;
            for (int i = 0; i < Wins.Count; i++)
            {
                Win w = Wins[i];
                double y = w.R.Top;
                var pieces = new List<double[]> { new[] { w.R.Left + inset, w.R.Right - inset } };
                // anything above this window in z-order that covers its top edge hides that stretch
                for (int j = 0; j < i && pieces.Count > 0; j++)
                {
                    RECT o = Wins[j].R;
                    if (o.Top <= y && o.Bottom > y) pieces = Subtract(pieces, o.Left, o.Right);
                }
                foreach (var m in Mons)
                {
                    if (y < m.Work.Top + headroom || y > m.Work.Bottom - 12) continue;
                    foreach (var p in pieces)
                    {
                        double a = Math.Max(p[0], m.Work.Left), b = Math.Min(p[1], m.Work.Right);
                        if (b - a >= minWidth)
                            Segs.Add(new Seg { Kind = SegKind.Plank, X1 = a, X2 = b, Y = y, Hwnd = w.Hwnd, App = w.App, Mon = m.Index, Z = w.Z });
                    }
                }
            }
        }

        static List<double[]> Subtract(List<double[]> pieces, double a, double b)
        {
            var result = new List<double[]>();
            foreach (var p in pieces)
            {
                if (b <= p[0] || a >= p[1]) { result.Add(p); continue; }
                if (a > p[0]) result.Add(new[] { p[0], a });
                if (b < p[1]) result.Add(new[] { b, p[1] });
            }
            return result;
        }

        // ---- queries ----

        public Mon MonAt(double x, double y)
        {
            foreach (var m in Mons)
                if (x >= m.Bounds.Left && x < m.Bounds.Right && y >= m.Bounds.Top - 2 && y <= m.Bounds.Bottom + 2) return m;
            Mon best = null; double bd = double.MaxValue;
            foreach (var m in Mons)
            {
                double dx = Math.Max(0, Math.Max(m.Bounds.Left - x, x - m.Bounds.Right));
                double dy = Math.Max(0, Math.Max(m.Bounds.Top - y, y - m.Bounds.Bottom));
                double d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = m; }
            }
            return best;
        }

        public Mon MonByIndex(int i) { return i >= 0 && i < Mons.Count ? Mons[i] : null; }

        public Seg Floor(int mon) { return Segs.FirstOrDefault(s => s.Kind == SegKind.Floor && s.Mon == mon); }
        public Seg Ceiling(int mon) { return Ceils.FirstOrDefault(s => s.Mon == mon); }

        /// <summary>The first surface you'd land on falling from (x, y).</summary>
        public Seg LandingBelow(double x, double y, Seg exclude)
        {
            Seg best = null;
            foreach (var s in Segs)
            {
                if (s == exclude || !s.Contains(x, 0) || s.Y < y) continue;
                if (best == null || s.Y < best.Y) best = s;
            }
            return best;
        }

        /// <summary>Find this scan's version of a surface the creature was standing on.</summary>
        public Seg Refind(Seg old, double x)
        {
            if (old == null) return null;
            Seg best = null; double bd = double.MaxValue;
            foreach (var s in Segs)
            {
                if (!s.SameAs(old)) continue;
                double d = s.Contains(x, 0) ? 0 : Math.Min(Math.Abs(x - s.X1), Math.Abs(x - s.X2));
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        public Win WinOf(IntPtr hwnd) { return Wins.FirstOrDefault(w => w.Hwnd == hwnd); }

        public double MinX { get { return Mons.Count == 0 ? 0 : Mons.Min(m => m.Bounds.Left); } }
        public double MaxX { get { return Mons.Count == 0 ? 1920 : Mons.Max(m => m.Bounds.Right); } }

        /// <summary>Plain-text summary for the assistant.</summary>
        public string Describe(int maxWindows)
        {
            var sb = new StringBuilder();
            sb.AppendLine("monitors: " + string.Join("; ", Mons.Select(m => "#" + (m.Index + 1) + " " + m.Bounds.Width + "x" + m.Bounds.Height + (m.Primary ? " (primary)" : ""))));
            if (Foreground != null) sb.AppendLine("focused window: [" + Foreground.App + "] " + Trim(Foreground.Title, 120));
            sb.AppendLine("open windows (front to back):");
            foreach (var w in Wins.Take(maxWindows))
                sb.AppendLine("- [" + w.App + "] " + Trim(w.Title, 90) + (w.Maximized ? " (maximized)" : ""));
            if (Wins.Count > maxWindows) sb.AppendLine("- ...and " + (Wins.Count - maxWindows) + " more");
            return sb.ToString();
        }

        static string Trim(string s, int n) { return s == null ? "" : s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }
    }
}
