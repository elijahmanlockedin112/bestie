// ClipWatch.cs - the last few things copied on the computer, so the phone app can grab any of them (not just the
// latest). Text only, in memory only (never written to disk), and it leaves out what shouldn't be kept: anything a
// password manager marks as private (the standard Windows clipboard flags), and strings that look like a password
// or key. C# 5 only; Start() on the UI thread.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Bestie
{
    internal sealed class ClipWatch : NativeWindow
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        const int WM_CLIPBOARDUPDATE = 0x031D;

        sealed class Item { public string Text; public DateTime At; }
        static readonly List<Item> items = new List<Item>();
        static ClipWatch instance;

        /// <summary>Starts listening (UI thread). Safe to call more than once.</summary>
        public static void Start()
        {
            if (instance != null) return;
            try
            {
                var w = new ClipWatch();
                w.CreateHandle(new CreateParams { Parent = new IntPtr(-3) });   // a message-only window
                if (!AddClipboardFormatListener(w.Handle)) { DebugLog.Write("clipboard: couldn't listen (" + Marshal.GetLastWin32Error() + ")"); w.DestroyHandle(); return; }
                instance = w;
            }
            catch (Exception ex) { DebugLog.Write("clipboard: " + ex.Message); }
        }

        public static void Stop()
        {
            if (instance == null) return;
            try { RemoveClipboardFormatListener(instance.Handle); instance.DestroyHandle(); } catch { }
            instance = null;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE) { try { Capture(); } catch { } }
            base.WndProc(ref m);
        }

        static void Capture()
        {
            IDataObject data = Clipboard.GetDataObject();
            if (data == null || !data.GetDataPresent(DataFormats.UnicodeText)) return;
            // password managers mark what they copy so clipboard history skips it; so do we
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
            if (data.GetDataPresent("CanIncludeInClipboardHistory"))
            {
                var flag = data.GetData("CanIncludeInClipboardHistory") as MemoryStream;
                if (flag != null && flag.Length >= 4) { var b = flag.ToArray(); if (BitConverter.ToInt32(b, 0) == 0) return; }
            }
            string text = data.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrWhiteSpace(text) || LooksSecret(text)) return;
            if (text.Length > 8000) text = text.Substring(0, 8000);
            lock (items)
            {
                items.RemoveAll(i => i.Text == text);
                items.Insert(0, new Item { Text = text, At = DateTime.UtcNow });
                while (items.Count > 10) items.RemoveAt(items.Count - 1);
            }
        }

        /// <summary>One "word" of 16+ characters mixing letters, digits and symbols: a password, token or key.</summary>
        static bool LooksSecret(string t)
        {
            t = t.Trim();
            if (t.Length < 16 || t.Length > 200 || Regex.IsMatch(t, @"\s") || Regex.IsMatch(t, @"^https?://")) return false;
            int kinds = (Regex.IsMatch(t, "[a-z]") ? 1 : 0) + (Regex.IsMatch(t, "[A-Z]") ? 1 : 0) + (Regex.IsMatch(t, "[0-9]") ? 1 : 0) + (Regex.IsMatch(t, @"[^A-Za-z0-9]") ? 1 : 0);
            return kinds >= 3 || Regex.IsMatch(t, @"^(sk|pk|ghp|gho|xox[abp]|AKIA|AIza)[-_A-Za-z0-9]{8,}");
        }

        /// <summary>Newest first, for the phone.</summary>
        public static object Recent()
        {
            lock (items)
                return items.Select(i => (object)new Dictionary<string, object> {
                    { "text", i.Text }, { "when", (DateTime.UtcNow - i.At).TotalMinutes < 1 ? "just now" : (DateTime.UtcNow - i.At).TotalHours < 1 ? (int)(DateTime.UtcNow - i.At).TotalMinutes + "m ago" : (int)(DateTime.UtcNow - i.At).TotalHours + "h ago" } }).ToArray();
        }
    }
}
