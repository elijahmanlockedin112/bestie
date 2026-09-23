using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>What the user wrote about themselves for the assistant to keep in mind when it answers.
    /// Private on purpose: stored encrypted (DPAPI, this Windows account) in .state\about-me.dat, never written to
    /// the log, the memory folder, notes or chats, and only ever sent to the AI as part of its instructions.</summary>
    public static class AboutMe
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("bestie about-me v1");
        static readonly object gate = new object();
        static string cached;
        /// <summary>Tests only: somewhere else to keep it.</summary>
#pragma warning disable 649   // only tests set it
        internal static string PathOverride;
#pragma warning restore 649

        static string StorePath { get { return PathOverride ?? Home.State("about-me.dat"); } }

        public static string Text
        {
            get
            {
                lock (gate)
                {
                    if (cached != null) return cached;
                    cached = "";
                    try
                    {
                        if (File.Exists(StorePath))
                            cached = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser));
                    }
                    catch (Exception ex) { DebugLog.Write("about me: couldn't read it (" + ex.GetType().Name + ")"); }
                    return cached;
                }
            }
        }

        public static void Save(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n").Trim();
            lock (gate)
            {
                if (text.Length == 0) { if (File.Exists(StorePath)) File.Delete(StorePath); }
                else
                {
                    string tmp = StorePath + ".tmp";
                    File.WriteAllBytes(tmp, ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser));
                    File.Copy(tmp, StorePath, true);
                    File.Delete(tmp);
                }
                cached = text;
            }
            DebugLog.Write("about me: saved (" + text.Length + " characters)");   // the length only, never the words
        }
    }

    /// <summary>The "about me" editor: a roomy text box and a promise.</summary>
    public sealed class AboutMeForm : Form
    {
        readonly TextBox box;
        readonly Label count;

        public AboutMeForm(string creatureName)
        {
            Text = "about you (private)";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; TopMost = true;
            ClientSize = new Size(560, 440);
            MinimumSize = new Size(420, 320);

            var intro = new Label
            {
                Left = 14, Top = 12, Width = 532, Height = 58, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Text = "tell " + creatureName + " about yourself: who you are, what you're into, what you're working on, how you like answers. "
                     + creatureName + " keeps it in mind every time it answers.",
            };
            var privacy = new Label
            {
                Left = 14, Top = 70, Width = 532, Height = 36, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                ForeColor = SystemColors.GrayText,
                Text = "private: saved encrypted on this computer and sent only to " + creatureName + "'s AI with each question. "
                     + "the text itself is never written to its log, memory folder, notes or chat history.",
            };
            box = new TextBox
            {
                Left = 14, Top = 110, Width = 532, Height = 276, Multiline = true, AcceptsReturn = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical, MaxLength = 0, Font = new Font("Segoe UI", 10f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = AboutMe.Text.Replace("\n", "\r\n"),
            };
            count = new Label { Left = 14, Top = 400, Width = 360, Height = 22, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var save = new Button { Text = "save", Left = 390, Top = 396, Width = 75, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "cancel", Left = 471, Top = 396, Width = 75, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
            Controls.AddRange(new Control[] { intro, privacy, box, count, save, cancel });
            CancelButton = cancel;   // (no AcceptButton: enter makes a new line)
            box.TextChanged += (s, e) => UpdateCount();
            UpdateCount();
            Shown += (s, e) => { Activate(); box.Focus(); box.SelectionStart = box.TextLength; };
        }

        void UpdateCount()
        {
            // no limit, but it rides along with every question, so show roughly what that costs in tokens (~4 characters each)
            count.Text = box.TextLength.ToString("N0") + " characters (~" + ((box.TextLength + 3) / 4).ToString("N0") + " tokens per question)";
        }

        public string Result { get { return box.Text; } }
    }
}
