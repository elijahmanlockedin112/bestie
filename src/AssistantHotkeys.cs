// AssistantHotkeys.cs - global hotkeys on a hidden message-only window. C# 5 only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Bestie
{
    internal static class HotkeyNative
    {
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
        public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    /// <summary>Create and use on the UI thread. Register returns "ok", "off" or a short problem.</summary>
    internal sealed class HotkeyManager : NativeWindow, IDisposable
    {
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();

        public HotkeyManager()
        {
            var cp = new CreateParams();
            cp.Caption = "bestie-hotkeys";
            cp.Parent = new IntPtr(-3);   // HWND_MESSAGE: message-only window
            CreateHandle(cp);
        }

        public string Register(int id, string spec, Action action)
        {
            Unregister(id);
            if (string.IsNullOrEmpty(spec) || spec.Trim().Length == 0) return "off";
            uint mods;
            Keys key;
            if (!TryParse(spec, out mods, out key)) return "i don't understand \"" + spec + "\"";
            if (!HotkeyNative.RegisterHotKey(Handle, id, mods | HotkeyNative.MOD_NOREPEAT, (uint)key))
            {
                int err = Marshal.GetLastWin32Error();
                return err == HotkeyNative.ERROR_HOTKEY_ALREADY_REGISTERED ? "taken by another app" : "couldn't register it (error " + err + ")";
            }
            actions[id] = action;
            return "ok";
        }

        public void Unregister(int id)
        {
            if (actions.Remove(id) && Handle != IntPtr.Zero) HotkeyNative.UnregisterHotKey(Handle, id);
        }

        public void UnregisterAll()
        {
            foreach (int id in actions.Keys.ToList()) Unregister(id);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == HotkeyNative.WM_HOTKEY)
            {
                Action a;
                if (actions.TryGetValue(m.WParam.ToInt32(), out a))
                {
                    try { a(); }
                    catch (Exception ex) { DebugLog.Write("assistant: hotkey action: " + ex); }
                }
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            try { UnregisterAll(); } catch { }
            try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch { }
        }

        /// <summary>"Ctrl+Alt+B" -> MOD_CONTROL|MOD_ALT, Keys.B. Needs at least one modifier.</summary>
        public static bool TryParse(string spec, out uint mods, out Keys key)
        {
            mods = 0;
            key = Keys.None;
            if (string.IsNullOrEmpty(spec)) return false;
            foreach (string raw in spec.Split('+'))
            {
                string p = raw.Trim();
                string l = p.ToLowerInvariant();
                if (l.Length == 0) continue;
                if (l == "ctrl" || l == "control") mods |= HotkeyNative.MOD_CONTROL;
                else if (l == "alt") mods |= HotkeyNative.MOD_ALT;
                else if (l == "shift") mods |= HotkeyNative.MOD_SHIFT;
                else if (l == "win" || l == "windows") mods |= HotkeyNative.MOD_WIN;
                else
                {
                    Keys k;
                    if (!TryKey(p, out k) || key != Keys.None) return false;
                    key = k;
                }
            }
            return key != Keys.None && mods != 0;
        }

        static bool TryKey(string p, out Keys k)
        {
            k = Keys.None;
            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if (c >= 'A' && c <= 'Z') { k = (Keys)c; return true; }
                if (c >= '0' && c <= '9') { k = Keys.D0 + (c - '0'); return true; }
            }
            string l = p.ToLowerInvariant();
            if (l == "space") { k = Keys.Space; return true; }
            if (l == "enter" || l == "return") { k = Keys.Enter; return true; }
            if (l == "esc" || l == "escape") { k = Keys.Escape; return true; }
            if (l == "`" || l == "backtick" || l == "tilde") { k = Keys.Oemtilde; return true; }
            int dummy;
            if (int.TryParse(p, out dummy)) return false;   // Enum.TryParse would accept raw numbers
            if (!Enum.TryParse<Keys>(p, true, out k)) return false;
            if ((k & Keys.Modifiers) != 0) return false;
            return k != Keys.None && k != Keys.ControlKey && k != Keys.ShiftKey && k != Keys.Menu && k != Keys.LWin && k != Keys.RWin;
        }

        public static string Format(Keys modifiers, Keys key)
        {
            var parts = new List<string>();
            if ((modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & Keys.Shift) != 0) parts.Add("Shift");
            string name;
            if (key >= Keys.D0 && key <= Keys.D9) name = ((int)(key - Keys.D0)).ToString();
            else if (key == Keys.Oemtilde) name = "`";
            else name = key.ToString();
            parts.Add(name);
            return string.Join("+", parts.ToArray());
        }
    }

    /// <summary>A text box that records a key combination instead of text.</summary>
    internal sealed class HotkeyTextBox : TextBox
    {
        public HotkeyTextBox()
        {
            ShortcutsEnabled = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.SuppressKeyPress = true;
            e.Handled = true;
            Keys k = e.KeyCode;
            if ((k == Keys.Back || k == Keys.Delete) && e.Modifiers == Keys.None) { Text = ""; return; }
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return;
            if ((e.Modifiers & (Keys.Control | Keys.Alt)) == 0) return;   // needs ctrl or alt
            Text = HotkeyManager.Format(e.Modifiers, k);
            SelectionStart = Text.Length;
        }

        protected override void OnKeyPress(KeyPressEventArgs e) { e.Handled = true; }
    }
}
