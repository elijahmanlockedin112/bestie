using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>Hold-to-talk on a key, anywhere in Windows: press and it's listening at once, let go and it sends at
    /// once (no wake word, no waiting for silence). A quick tap listens hands-free instead. The key itself is swallowed
    /// so the app in front doesn't also get it. Several keys can be set: the default is "F9, MediaNextTrack", because on
    /// many laptops the F9 key sends "next track" unless Fn is held. Install on the UI thread.</summary>
    internal sealed class TalkKey : IDisposable
    {
        public const string Default = "F9, MediaNextTrack";

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        /// <summary>Stamped on every key and click Bestie itself sends (SendInput's extra info), so its own input is never
        /// mistaken for the user's. Anything else counts, including keys a laptop's hotkey software sends on its behalf.</summary>
        public static readonly IntPtr OwnInput = (IntPtr)0x42455354;

        const int TapMs = 350;
        readonly HookProc proc;   // kept alive here, or the hook would call into collected memory
        readonly HashSet<uint> vks;
        readonly Action down, upHold, upTap;
        readonly Control ui;
        IntPtr hook;
        uint hookThread;
        uint heldVk;              // the key that's down right now (0 = none)
        int downAt;
        static int seenLogged;

        /// <summary>down: start listening (hold mode). upHold: let go after a real hold, send it. upTap: it was a tap.
        /// The hook lives on its own thread with its own message loop: windows skips (and can quietly remove) a keyboard
        /// hook whose thread is slow to answer, and the creature's thread is busy drawing.</summary>
        public TalkKey(Keys[] keys, Control ui, Action down, Action upHold, Action upTap)
        {
            vks = new HashSet<uint>(keys.Select(k => (uint)k));
            this.ui = ui; this.down = down; this.upHold = upHold; this.upTap = upTap;
            proc = Filter;
            var ready = new System.Threading.ManualResetEvent(false);
            var t = new System.Threading.Thread(delegate()
            {
                hookThread = GetCurrentThreadId();
                hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
                ready.Set();
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            });
            t.IsBackground = true;
            t.Name = "bestie-talk-key";
            t.Start();
            ready.WaitOne(2000);
        }

        public bool Active { get { return hook != IntPtr.Zero; } }

        /// <summary>"F9, MediaNextTrack" -> those keys (each one on its own, no modifiers). Unknown names are skipped.</summary>
        public static Keys[] Parse(string text)
        {
            var list = new List<Keys>();
            foreach (string part in (text ?? "").Split(','))
            {
                Keys key;
                string p = part.Trim();
                if (p.Length > 0 && !p.Contains("+") && Enum.TryParse(p, true, out key) && Usable(key) && !list.Contains(key)) list.Add(key);
            }
            return list.ToArray();
        }

        /// <summary>Keys that make sense to hold: function keys, media and browser keys, and a few spares. Not the keys
        /// you type with, and not ctrl/alt/shift/win on their own.</summary>
        public static bool Usable(Keys k)
        {
            return k >= Keys.F1 && k <= Keys.F24
                || k >= Keys.BrowserBack && k <= Keys.LaunchApplication2   // browser, volume, media and launch keys
                || k == Keys.Pause || k == Keys.Scroll || k == Keys.Insert || k == Keys.PrintScreen
                || k == Keys.RControlKey || k == Keys.RMenu || k == Keys.Apps || k == Keys.CapsLock;
        }

        IntPtr Filter(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool ours = k.extra == OwnInput;
                int msg = (int)wParam;
                // what the special keys really send (function, media, browser keys only: never typing), for the log
                if ((msg == 0x100 || msg == 0x104) && seenLogged < 12 && (k.vkCode >= 0x70 && k.vkCode <= 0x87 || k.vkCode >= 0xA6 && k.vkCode <= 0xB7))
                {
                    seenLogged++;
                    string what = ((Keys)k.vkCode) + " (injected " + ((k.flags & 0x10) != 0) + ", " + (vks.Contains(k.vkCode) ? "the talk key" : "not the talk key") + ")";
                    System.Threading.ThreadPool.QueueUserWorkItem(_ => DebugLog.Write("talk key: saw " + what));
                }
                if (vks.Contains(k.vkCode) && !ours)
                {
                    if (msg == 0x100 || msg == 0x104)   // key down (repeats keep coming while it's held)
                    {
                        // with ctrl/alt/shift/win held it belongs to whatever shortcut they're pressing
                        bool modded = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0
                                   || (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0;
                        if (heldVk == 0 && modded) return CallNextHookEx(hook, code, wParam, lParam);
                        if (heldVk == 0) { heldVk = k.vkCode; downAt = Environment.TickCount; Post(down); }
                        return (IntPtr)1;
                    }
                    if (msg == 0x101 || msg == 0x105)   // key up
                    {
                        if (heldVk == k.vkCode) { heldVk = 0; Post(Environment.TickCount - downAt < TapMs ? upTap : upHold); }
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        void Post(Action a)
        {
            // the hook has to return fast; the real work happens just after
            try { if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a); } catch { }
        }

        public void Dispose()
        {
            if (hookThread != 0) { PostThreadMessage(hookThread, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); hookThread = 0; }
        }

        /// <summary>The next key pressed anywhere (media keys included, which windows don't always see), swallowed.
        /// For the settings box. Install on the UI thread; dispose when done.</summary>
        internal sealed class Catcher : IDisposable
        {
            readonly HookProc proc;
            readonly Action<Keys> got;
            readonly Control ui;
            IntPtr hook;

            public Catcher(Control ui, Action<Keys> got)
            {
                this.ui = ui; this.got = got;
                proc = Filter;
                hook = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
            }

            IntPtr Filter(int code, IntPtr wParam, IntPtr lParam)
            {
                if (code >= 0 && ((int)wParam == 0x100 || (int)wParam == 0x104))
                {
                    var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    var key = (Keys)k.vkCode;
                    if (Usable(key) || key == Keys.Back || key == Keys.Delete)
                    {
                        try { ui.BeginInvoke(new Action(() => got(key))); } catch { }
                        return (IntPtr)1;
                    }
                }
                return CallNextHookEx(hook, code, wParam, lParam);
            }

            public void Dispose()
            {
                if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            }
        }
    }

    /// <summary>Settings box for the talk key: click it and press the key (function, media, browser keys...). Backspace clears.</summary>
    internal sealed class TalkKeyBox : TextBox
    {
        TalkKey.Catcher catcher;

        public TalkKeyBox() { ReadOnly = true; BackColor = System.Drawing.SystemColors.Window; ShortcutsEnabled = false; }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (catcher == null) catcher = new TalkKey.Catcher(this, k => { Text = k == Keys.Back || k == Keys.Delete ? "" : k.ToString(); });
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            if (catcher != null) { catcher.Dispose(); catcher = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (catcher != null) { catcher.Dispose(); catcher = null; }
            base.Dispose(disposing);
        }
    }
}
