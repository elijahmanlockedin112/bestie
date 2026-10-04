using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>Hold-to-talk on one key (default F9), anywhere in Windows: press and it's listening at once, let go
    /// and it sends at once (no wake word, no waiting for silence). A quick tap listens hands-free instead. The key
    /// itself is swallowed so the app in front doesn't also get it. Install on the UI thread.</summary>
    internal sealed class TalkKey : IDisposable
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        const int TapMs = 350;
        readonly HookProc proc;   // kept alive here, or the hook would call into collected memory
        readonly uint vk;
        readonly Action down, upHold, upTap;
        readonly Control ui;
        IntPtr hook;
        bool held;
        int downAt;

        /// <summary>down: start listening (hold mode). upHold: let go after a real hold, send it. upTap: it was a tap.</summary>
        public TalkKey(Keys key, Control ui, Action down, Action upHold, Action upTap)
        {
            vk = (uint)key;
            this.ui = ui; this.down = down; this.upHold = upHold; this.upTap = upTap;
            proc = Filter;
            hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
        }

        public bool Active { get { return hook != IntPtr.Zero; } }

        /// <summary>"F9" -> Keys.F9 (one key on its own), or Keys.None.</summary>
        public static Keys Parse(string text)
        {
            Keys key;
            if (string.IsNullOrWhiteSpace(text) || text.Contains("+") || !Enum.TryParse(text.Trim(), true, out key)) return Keys.None;
            return Usable(key) ? key : Keys.None;
        }

        /// <summary>Keys that make sense to hold (not letters you type with, not modifiers on their own).</summary>
        public static bool Usable(Keys k)
        {
            return k >= Keys.F1 && k <= Keys.F24 || k == Keys.Pause || k == Keys.Scroll || k == Keys.Insert
                || k == Keys.RControlKey || k == Keys.RMenu || k == Keys.Apps || k == Keys.CapsLock;
        }

        IntPtr Filter(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool injected = (k.flags & 0x10) != 0;
                // only the bare key: with ctrl/alt/shift/win held it belongs to whatever shortcut they're pressing
                bool modded = (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0
                           || (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0;
                if (k.vkCode == vk && !injected && (held || !modded))
                {
                    int msg = (int)wParam;
                    if (msg == 0x100 || msg == 0x104)   // key down (repeats keep coming while it's held)
                    {
                        if (!held) { held = true; downAt = Environment.TickCount; Post(down); }
                        return (IntPtr)1;
                    }
                    if (msg == 0x101 || msg == 0x105)   // key up
                    {
                        if (held) { held = false; Post(Environment.TickCount - downAt < TapMs ? upTap : upHold); }
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
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }
    }

    /// <summary>Settings box for the talk key: click it and press one key (F1-F24, Pause, Insert...). Backspace clears.</summary>
    internal sealed class TalkKeyBox : TextBox
    {
        public TalkKeyBox() { ReadOnly = true; BackColor = System.Drawing.SystemColors.Window; ShortcutsEnabled = false; }

        protected override bool IsInputKey(Keys keyData) { return true; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete) { Text = ""; return; }
            if (TalkKey.Usable(e.KeyCode)) Text = e.KeyCode.ToString();
        }
    }
}
