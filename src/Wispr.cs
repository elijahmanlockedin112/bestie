using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bestie
{
    public class Dictation
    {
        public long Id;
        public DateTime When;
        public string App, Url, Text;
    }

    /// <summary>Plugs into the real Wispr Flow app (not an API): presses its push-to-talk keys for us, reads what it
    /// typed from its local history, and notices when you're dictating anywhere. Everything here fails quietly:
    /// Wispr's internals can change with an update.</summary>
    public static class Wispr
    {
        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow");
        static string DbPath { get { return Path.Combine(DataDir, "flow.sqlite"); } }

        public static bool Installed { get { return File.Exists(Path.Combine(DataDir, "config.json")); } }

        static bool running; static DateTime runningChecked;
        static string exePath;
        public static bool Running
        {
            get
            {
                if ((DateTime.Now - runningChecked).TotalSeconds > 5)
                {
                    runningChecked = DateTime.Now;
                    var procs = Process.GetProcessesByName("Wispr Flow");
                    running = procs.Length > 0;
                    if (running && exePath == null) { try { exePath = procs[0].MainModule.FileName; } catch { } }
                    foreach (var p in procs) p.Dispose();
                }
                return running;
            }
        }

        // ---- its push-to-talk shortcut (from its own config), and pressing it ----

        static ushort[] ptt;
        public static ushort[] PttKeys()
        {
            if (ptt != null) return ptt;
            ptt = new ushort[] { 0xA2, 0x5B };   // left ctrl + left win: wispr's default on windows
            try
            {
                string cfg = File.ReadAllText(Path.Combine(DataDir, "config.json"));
                var m = Regex.Match(cfg, "\"([0-9+]+)\"\\s*:\\s*\"ptt\"");
                if (m.Success) ptt = m.Groups[1].Value.Split('+').Select(k => (ushort)int.Parse(k)).ToArray();
            }
            catch (Exception ex) { DebugLog.Write("wispr: couldn't read its shortcut, using ctrl+win (" + ex.Message + ")"); }
            return ptt;
        }

        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        static void Key(ushort vk, bool up)
        {
            var i = new INPUT { type = 1 };
            i.ki.wVk = vk;
            i.ki.dwFlags = up ? 2u : 0u;
            SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void PressPtt() { foreach (var k in PttKeys()) Key(k, false); }

        public static void ReleasePtt()
        {
            var keys = PttKeys();
            // tap an unused key first, or letting go of the windows key on its own opens the start menu
            if (keys.Any(k => k == 0x5B || k == 0x5C)) { Key(0xFF, false); Key(0xFF, true); }
            foreach (var k in keys.Reverse()) Key(k, true);
        }

        /// <summary>Is the user dictating with Wispr right now (anywhere)? Checks its hotkey and Windows' "mic in use" record.</summary>
        public static bool IsDictating()
        {
            try
            {
                var keys = PttKeys();
                if (keys.Length > 0 && keys.All(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return true;
                if (exePath == null) return false;
                string sub = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged\" + exePath.Replace('\\', '#');
                using (var k = Registry.CurrentUser.OpenSubKey(sub))
                {
                    if (k == null) return false;
                    long start = Convert.ToInt64(k.GetValue("LastUsedTimeStart") ?? 0L), stop = Convert.ToInt64(k.GetValue("LastUsedTimeStop") ?? 0L);
                    return start > 0 && (stop == 0 || start > stop);
                }
            }
            catch { return false; }
        }

        // ---- its history (a local sqlite file), read with Windows' built-in sqlite ----

        public static long LatestId()
        {
            var rows = Query("select max(rowid) from History", r => r.Long(0));
            return rows.Count > 0 ? rows[0] : 0;
        }

        public static List<Dictation> Since(long afterId, int max)
        {
            return Query("select rowid, timestamp, app, url, coalesce(nullif(formattedText,''), nullif(asrText,''), '') from History where rowid > " + afterId + " order by rowid limit " + max,
                r => new Dictation { Id = r.Long(0), When = ParseTime(r.Text(1)), App = r.Text(2), Url = r.Text(3), Text = r.Text(4) });
        }

        public static List<Dictation> Recent(int max, TimeSpan within)
        {
            var list = Query("select rowid, timestamp, app, url, coalesce(nullif(formattedText,''), nullif(asrText,''), '') from History order by rowid desc limit " + (max * 3),
                r => new Dictation { Id = r.Long(0), When = ParseTime(r.Text(1)), App = r.Text(2), Url = r.Text(3), Text = r.Text(4) });
            return list.Where(d => d.Text.Length > 0 && DateTime.Now - d.When < within && !IsOurs(d)).Take(max).ToList();
        }

        public static bool IsOurs(Dictation d) { return string.Equals(d.App, "bestie", StringComparison.OrdinalIgnoreCase); }

        static DateTime ParseTime(string s)
        {
            DateTime t;
            return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out t) ? t.ToLocalTime() : DateTime.MinValue;
        }

        class Row
        {
            public IntPtr Stmt;
            public string Text(int i) { IntPtr p = sqlite3_column_text(Stmt, i); int n = sqlite3_column_bytes(Stmt, i); if (p == IntPtr.Zero) return ""; var b = new byte[n]; Marshal.Copy(p, b, 0, n); return Encoding.UTF8.GetString(b); }
            public long Long(int i) { long v; long.TryParse(Text(i), out v); return v; }
        }

        static readonly object dbGate = new object();
        static bool dbComplained;

        static List<T> Query<T>(string sql, Func<Row, T> read)
        {
            var result = new List<T>();
            if (!File.Exists(DbPath)) return result;
            lock (dbGate)
            {
                IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
                try
                {
                    // read-only, and never write the -shm/-wal files wispr owns
                    if (sqlite3_open_v2(Utf8("file:" + DbPath.Replace('\\', '/') + "?mode=ro"), out db, 0x01 | 0x40, IntPtr.Zero) != 0) throw new IOException("open failed");
                    sqlite3_busy_timeout(db, 500);
                    if (sqlite3_prepare_v2(db, Utf8(sql), -1, out stmt, IntPtr.Zero) != 0) throw new IOException(Marshal.PtrToStringAnsi(sqlite3_errmsg(db)));
                    var row = new Row { Stmt = stmt };
                    while (sqlite3_step(stmt) == 100) result.Add(read(row));
                }
                catch (Exception ex)
                {
                    if (!dbComplained) { dbComplained = true; DebugLog.Write("wispr: can't read its history (" + ex.Message + ")"); }
                }
                finally
                {
                    if (stmt != IntPtr.Zero) sqlite3_finalize(stmt);
                    if (db != IntPtr.Zero) sqlite3_close(db);
                }
            }
            return result;
        }

        static byte[] Utf8(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_text(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_errmsg(IntPtr db);

        // ---- "hey mothball …" inside an ordinary dictation ----

        static readonly Regex Greeting = new Regex(@"^\W*(hi|hey|hello|okay|ok|yo)?[\s,]*", RegexOptions.IgnoreCase);

        /// <summary>If the dictation starts by addressing it ("hey blorp, …" / "blorp …"), returns the command part (maybe empty); else null.</summary>
        public static string Addressed(string text, IEnumerable<string> names)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim();
            var g = Greeting.Match(t);
            string rest = t.Substring(g.Length);
            foreach (var n in names.OrderByDescending(n => n.Length))
            {
                if (!rest.StartsWith(n, StringComparison.OrdinalIgnoreCase)) continue;
                if (rest.Length > n.Length && char.IsLetterOrDigit(rest[n.Length])) continue;   // "bestiessss", "mothballs"
                return rest.Substring(n.Length).TrimStart(',', '.', '!', '?', ' ', '—', '-').Trim();
            }
            return null;
        }
    }

    /// <summary>Watches Wispr's history in the background and reports new finished dictations (UI thread).</summary>
    public sealed class WisprWatcher : IDisposable
    {
        readonly Control ui;
        System.Threading.Timer timer;
        long lastId = -1;
        readonly Dictionary<long, DateTime> pending = new Dictionary<long, DateTime>();   // rows still being written
        int busy;

        public event Action<Dictation> Dictated;

        public WisprWatcher(Control ui) { this.ui = ui; }

        public void Start() { if (timer == null) timer = new System.Threading.Timer(_ => Poll(), null, 1000, 1200); }

        void Poll()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                if (lastId < 0) { lastId = Wispr.LatestId(); return; }   // only what happens from now on
                long from = pending.Count > 0 ? Math.Min(lastId, pending.Keys.Min() - 1) : lastId;
                foreach (var d in Wispr.Since(from, 20))
                {
                    if (d.Id > lastId) lastId = d.Id;
                    if (d.Text.Length == 0)
                    {
                        // wispr writes the row first and the text a moment later; give it up to a minute
                        DateTime first;
                        if (!pending.TryGetValue(d.Id, out first)) pending[d.Id] = DateTime.Now;
                        else if ((DateTime.Now - first).TotalSeconds > 60) pending.Remove(d.Id);
                        continue;
                    }
                    bool wasPending = pending.Remove(d.Id);
                    if (d.Id <= from && !wasPending) continue;
                    var copy = d;
                    try { if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(new Action(() => { if (Dictated != null) Dictated(copy); })); } catch { }
                }
            }
            catch (Exception ex) { DebugLog.Write("wispr watcher: " + ex.Message); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        public void Dispose() { if (timer != null) { timer.Dispose(); timer = null; } }
    }

    /// <summary>A little "listening through wispr" box near the creature. While you hold the creature, it presses Wispr's
    /// push-to-talk for you and catches what Wispr types into it.</summary>
    public sealed class WisprCapture : Form
    {
        readonly TextBox box;
        readonly System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer { Interval = 150 };
        IntPtr previous;
        DateTime releasedAt, lastChange;
        Action<string> done;
        bool holding;

        public bool Active { get; private set; }

        public WisprCapture()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(252, 248, 238);
            Padding = new Padding(10, 8, 10, 8);
            Size = new Size(320, 64);
            var label = new Label { Text = "listening through wispr…", Dock = DockStyle.Top, Height = 18, ForeColor = Color.FromArgb(140, 130, 140), Font = new Font("Segoe UI", 8.5f, FontStyle.Italic) };
            box = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = BackColor, Font = new Font("Segoe UI", 10.5f), Multiline = true };
            Controls.Add(box);
            Controls.Add(label);
            box.TextChanged += (s, e) => lastChange = DateTime.Now;
            settle.Tick += (s, e) => Settle();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        /// <summary>Start: take focus, press Wispr's keys. anchor = where to show the box (screen coords).
        /// False if the box couldn't get the focus: then nothing is pressed, because Wispr would type into whatever has it.</summary>
        public bool Begin(Point anchor, Action<string> onText)
        {
            if (Active) return true;
            previous = Native.GetForegroundWindow();
            box.Text = "";
            float k = DeviceDpi / 96f;
            Size = new Size((int)(320 * k), (int)(64 * k));
            Location = new Point(anchor.X - Width / 2, anchor.Y - Height - (int)(90 * k));
            try { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            Show();
            TakeFocus();
            if (Native.GetForegroundWindow() != Handle) { TakeFocus(); }
            if (Native.GetForegroundWindow() != Handle)
            {
                Hide();
                DebugLog.Write("wispr: couldn't get the focus for its text, so not using it this time");
                return false;
            }
            Active = true; holding = true; done = onText;
            Wispr.PressPtt();
            return true;
        }

        /// <summary>Windows only lets the app you're using hand the focus over, so borrow its input queue for a moment
        /// (the talk key is pressed while some other app is in front).</summary>
        void TakeFocus()
        {
            IntPtr fg = Native.GetForegroundWindow();
            uint pid;
            uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out pid), me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(Handle);
                Native.SetForegroundWindow(Handle);
                Activate();
                box.Focus();
            }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
        }

        /// <summary>The user let go: release Wispr's keys and wait for its text to land.</summary>
        public void End()
        {
            if (!Active || !holding) return;
            holding = false;
            Wispr.ReleasePtt();
            releasedAt = DateTime.Now;
            lastChange = DateTime.MinValue;
            settle.Start();
        }

        void Settle()
        {
            double since = (DateTime.Now - releasedAt).TotalSeconds;
            bool gotText = box.Text.Trim().Length > 0;
            // done when the text has stopped changing for a moment, or wispr never typed anything
            if ((gotText && (DateTime.Now - lastChange).TotalMilliseconds > 450) || since > 8)
            {
                settle.Stop();
                string text = box.Text.Trim();
                Hide();
                Active = false;
                if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
                var d = done; done = null;
                if (d != null) d(text);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) settle.Dispose();
            base.Dispose(disposing);
        }
    }
}
