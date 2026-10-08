// AssistantSettingsForm.cs - small, friendly settings window for the assistant. C# 5 only. UI thread.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Bestie
{
    internal sealed class AssistantSettingsForm : Form
    {
        static readonly Color Cream = Color.FromArgb(255, 250, 242);
        static readonly Color Ink = Color.FromArgb(52, 46, 40);
        static readonly Color Warm = Color.FromArgb(176, 84, 32);
        static readonly Color Muted = Color.FromArgb(134, 122, 110);
        static readonly Color Good = Color.FromArgb(46, 125, 50);
        static readonly Color Bad = Color.FromArgb(190, 50, 40);

        static readonly string[] EffortLabels = { "none (fastest, cheapest)", "minimal", "low", "medium", "high", "don't send it" };

        readonly AssistantModule owner;
        readonly AssistantSettings s;
        readonly float scale;
        readonly Font font, bold, small;
        readonly TableLayoutPanel table;
        readonly TextBox keyBox, baseUrlBox;
        readonly Label keyHint, testResult, browserStatus, saveResult;
        readonly Button testButton;
        static readonly string[] EngineLabels = { "openai voice (sounds real, ~1.5¢ a minute of talking)", "windows voice (free, a bit robotic)",
            "elevenlabs voice (the most natural; needs an elevenlabs key)" };

        readonly ComboBox modelBox, effortBox, transcribeBox, voiceBox, engineBox, openAiVoiceBox, ttsModelBox, elevenVoiceBox;
        readonly TextBox elevenKeyBox;
        readonly CheckBox elevenEarsBox;
        readonly TextBox alexaTokenBox, alexaDeviceBox;
        readonly ComboBox alexaWhenBox;
        readonly Label alexaStatus;
        readonly Button alexaTestButton;
        static readonly string[] AlexaWhenLabels = { "say them on it when i'm away from the computer", "always say them on it too", "only when i ask" };
        readonly List<string> elevenVoiceIds = new List<string>();
        readonly CheckBox speakBox, screenBox, tabsBox, autoLookBox, nosyBox, speakNosyBox;
        readonly NumericUpDown rateBox, historyBox, nosyMinutesBox;
        readonly TextBox skipBox, gIdBox, gSecretBox;
        readonly TrackBar gainBar;
        readonly Label gainLabel, calStatus;
        readonly CheckBox calWarnBox, autoMemoryBox, wakeBox, followUpBox;
        readonly TalkKeyBox talkKeyBox;
        readonly Button connectButton, disconnectButton;
        bool calBusy;
        readonly HotkeyTextBox[] hotkeyBoxes = new HotkeyTextBox[3];
        readonly Label[] hotkeyLabels = new Label[3];
        readonly System.Windows.Forms.Timer browserTimer;
        bool keyCleared;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        int S(double px) { return (int)Math.Round(px * scale); }

        /// <summary>area: work area of the monitor it opens on (the form scrolls if it wouldn't fit).</summary>
        public AssistantSettingsForm(AssistantModule owner, AssistantSettings settings, float scale, Rectangle area)
        {
            this.owner = owner;
            s = settings;
            this.scale = scale < 0.5f || scale > 5f ? 1f : scale;
            font = new Font("Segoe UI", 13.5f * this.scale, FontStyle.Regular, GraphicsUnit.Pixel);
            bold = new Font("Segoe UI", 13.5f * this.scale, FontStyle.Bold, GraphicsUnit.Pixel);
            small = new Font("Segoe UI", 12f * this.scale, FontStyle.Regular, GraphicsUnit.Pixel);

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = "my settings";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Cream;
            ForeColor = Ink;
            Font = font;
            AutoSize = false;

            var scroller = new Panel();
            scroller.Dock = DockStyle.Fill;
            scroller.AutoScroll = true;
            table = new TableLayoutPanel();
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.Location = new Point(S(14), S(10));

            // --- brain key
            keyBox = new TextBox();
            keyBox.UseSystemPasswordChar = true;
            keyBox.Width = S(300);
            keyBox.HandleCreated += delegate { SendMessage(keyBox.Handle, 0x1501, new IntPtr(1), "paste your key (sk-…)"); };
            testButton = Btn("test", delegate { OnTest(); });
            Row("brain key", keyBox, testButton);
            keyHint = Note(KeyHintText(), Muted);
            var forgetKey = new LinkLabel();
            forgetKey.AutoSize = true;
            forgetKey.Font = small;
            forgetKey.Text = "forget the saved key";
            forgetKey.LinkColor = Warm;
            forgetKey.Visible = s.KeySource == "saved";
            forgetKey.LinkClicked += delegate
            {
                keyCleared = true;
                keyBox.Text = "";
                keyHint.Text = "the saved key will be forgotten when you press save.";
                forgetKey.Visible = false;
            };
            Row("", Stack(keyHint, forgetKey));
            testResult = Note("", Muted);
            Row("", testResult);

            // --- models
            modelBox = Combo(AssistantSettings.SuggestedModels, s.Model, true, S(180));
            effortBox = Combo(EffortLabels, null, false, S(190));
            int ei = Array.IndexOf(AssistantSettings.ReasoningChoices, s.ReasoningEffort ?? "");
            effortBox.SelectedIndex = ei >= 0 ? ei : 0;
            Row("model", modelBox, Lbl("thinking", Muted), effortBox);
            Row("", Note("gpt-6-luna is cheap and quick. bigger models cost more per message.", Muted));
            transcribeBox = Combo(AssistantSettings.SuggestedTranscribeModels, s.TranscribeModel, true, S(180));
            Row("ears", transcribeBox, Lbl("(speech-to-text model)", Muted));
            baseUrlBox = new TextBox();
            baseUrlBox.Width = S(300);
            baseUrlBox.Text = s.BaseUrl;
            Row("api address", baseUrlBox);

            // --- voice
            speakBox = Check("read my replies out loud", s.SpeakReplies);
            Row("talking", speakBox);
            engineBox = Combo(EngineLabels, null, false, S(330));
            engineBox.SelectedIndex = s.VoiceEngine == "windows" ? 1 : s.VoiceEngine == "elevenlabs" ? 2 : 0;
            Row("voice", engineBox, Btn("try", delegate { OnTryVoice(); }));
            openAiVoiceBox = Combo(AssistantSettings.OpenAiVoices, null, false, S(120));
            if (!string.IsNullOrEmpty(s.OpenAiVoice) && openAiVoiceBox.Items.IndexOf(s.OpenAiVoice) < 0) openAiVoiceBox.Items.Add(s.OpenAiVoice);
            openAiVoiceBox.SelectedIndex = Math.Max(0, openAiVoiceBox.Items.IndexOf(s.OpenAiVoice ?? AssistantSettings.DefaultOpenAiVoice));
            ttsModelBox = Combo(AssistantSettings.SuggestedTtsModels, s.TtsModel, true, S(170));
            Row("", Lbl("openai", Ink, S(70)), openAiVoiceBox, ttsModelBox);
            // elevenlabs: its own key, a voice from the account, and (optionally) its ears too
            elevenKeyBox = new TextBox();
            elevenKeyBox.UseSystemPasswordChar = true;
            elevenKeyBox.Width = S(210);
            bool hadElevenKey = s.ElevenKey.Length > 0;
            elevenKeyBox.HandleCreated += delegate { SendMessage(elevenKeyBox.Handle, 0x1501, new IntPtr(1), hadElevenKey ? "key saved (paste to replace)" : "paste your elevenlabs key"); };
            elevenKeyBox.TextChanged += delegate
            {
                // a first key: they want elevenlabs, so switch it on (voice and ears); they can change it back
                if (!hadElevenKey && elevenKeyBox.Text.Trim().Length > 10) { engineBox.SelectedIndex = 2; elevenEarsBox.Checked = true; }
            };
            Row("", Lbl("elevenlabs", Ink, S(70)), elevenKeyBox, Btn("load voices", delegate { OnLoadElevenVoices(); }));
            elevenVoiceBox = Combo(new[] { s.ElevenVoiceName.Length > 0 ? s.ElevenVoiceName : "(first voice on the account)" }, null, false, S(250));
            elevenVoiceBox.SelectedIndex = 0;
            elevenVoiceIds.Add(s.ElevenVoice ?? "");
            elevenEarsBox = Check("use elevenlabs to hear me too", s.TranscribeEngine == "elevenlabs");
            Row("", Lbl("", Ink, S(70)), elevenVoiceBox);
            Row("", Lbl("", Ink, S(70)), elevenEarsBox);
            string[] voices = VoiceTts.InstalledVoices();
            voiceBox = Combo(new[] { "(default voice)" }, null, false, S(210));
            foreach (string v in voices) voiceBox.Items.Add(v);
            int vi = string.IsNullOrEmpty(s.TtsVoice) ? 0 : voiceBox.Items.IndexOf(s.TtsVoice);
            voiceBox.SelectedIndex = vi >= 0 ? vi : 0;
            rateBox = Num(-10, 10, s.TtsRate, S(56));
            Row("", Lbl("windows", Ink, S(70)), voiceBox, Lbl("speed", Muted), rateBox);
            gainBar = new TrackBar();
            gainBar.Minimum = 100;
            gainBar.Maximum = 400;
            gainBar.SmallChange = 10;
            gainBar.LargeChange = 20;
            gainBar.TickFrequency = 50;
            gainBar.Width = S(200);
            gainBar.Value = Math.Max(100, Math.Min(400, s.VoiceGainPercent));
            gainLabel = Lbl(gainBar.Value + "%", Muted);
            gainBar.ValueChanged += delegate { gainLabel.Text = gainBar.Value + "%"; };
            Row("", Lbl("loudness", Ink, S(70)), gainBar, gainLabel);
            Row("", Note("no key, or the big outside is down? i use the windows voice instead.", Muted));

            // --- alexa (voice monkey): reminders out loud on their echo
            alexaTokenBox = new TextBox();
            alexaTokenBox.UseSystemPasswordChar = true;
            alexaTokenBox.Width = S(210);
            bool hadAlexaToken = s.AlexaToken.Length > 0;
            alexaTokenBox.HandleCreated += delegate { SendMessage(alexaTokenBox.Handle, 0x1501, new IntPtr(1), hadAlexaToken ? "token saved (paste to replace)" : "voice monkey token"); };
            alexaTestButton = Btn("test", delegate { OnTestAlexa(); });
            var alexaHow = new LinkLabel();
            alexaHow.AutoSize = true;
            alexaHow.Font = small;
            alexaHow.Text = "how do i set this up?";
            alexaHow.LinkColor = Warm;
            alexaHow.Margin = new Padding(S(6), S(6), 0, 0);
            alexaHow.LinkClicked += delegate { MessageBox.Show(this, Alexa.SetupSteps, "alexa setup", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            Row("alexa", alexaTokenBox, alexaTestButton, alexaHow);
            alexaDeviceBox = new TextBox();
            alexaDeviceBox.Width = S(210);
            alexaDeviceBox.Text = s.AlexaDevice ?? Alexa.DefaultDevice;
            Row("", Lbl("speaker", Ink, S(70)), alexaDeviceBox);
            alexaWhenBox = Combo(AlexaWhenLabels, null, false, S(330));
            alexaWhenBox.SelectedIndex = s.AlexaWhen == "always" ? 1 : s.AlexaWhen == "asked" ? 2 : 0;
            Row("", Lbl("reminders", Ink, S(70)), alexaWhenBox);
            alexaStatus = Note(hadAlexaToken ? "connected through voice monkey." : "say reminders out loud on your echo. free; needs the voice monkey alexa skill.", Muted);
            Row("", alexaStatus);

            // --- hotkeys
            string[] hkNames = { "chat", "voice", "screenshot" };
            string[] hkValues = { s.HotkeyChat, s.HotkeyVoice, s.HotkeyScreen };
            for (int i = 0; i < 3; i++)
            {
                hotkeyBoxes[i] = new HotkeyTextBox();
                hotkeyBoxes[i].Width = S(150);
                hotkeyBoxes[i].Text = hkValues[i] ?? "";
                hotkeyLabels[i] = Lbl("", Muted);
                Row(i == 0 ? "hotkeys" : "", Lbl(hkNames[i], Ink, S(78)), hotkeyBoxes[i], hotkeyLabels[i]);
            }
            Row("", Note("click a box and press the keys. backspace clears it.", Muted));
            talkKeyBox = new TalkKeyBox();
            talkKeyBox.Width = S(150);
            talkKeyBox.Text = s.TalkKey ?? "";
            Row("hold to talk", talkKeyBox, Lbl("hold it, talk, let go. a tap listens hands-free.", Muted));
            RefreshHotkeyStatus();

            // --- permissions
            autoLookBox = Check("always look at my screens when i ask something", s.AutoLookScreens);
            screenBox = Check("look at my screen when you ask me to", s.AllowScreenTool);
            tabsBox = Check("read my browser tabs when you ask me to", s.AllowTabReading);
            Row("allowed", autoLookBox);
            Row("", screenBox);
            Row("", tabsBox);
            historyBox = Num(4, 200, s.MaxHistoryMessages, S(60));
            Row("memory", Lbl("keep the last", Ink), historyBox, Lbl("messages of a chat in my head", Ink));
            autoMemoryBox = Check("remember things from our chats automatically", s.AutoMemory);
            Row("", autoMemoryBox);
            string cname = "bestie";
            try { cname = owner.Host.CreatureName ?? cname; } catch { }
            wakeBox = Check("listen for my wake word (hey " + cname.ToLowerInvariant() + ")", s.WakeWord);
            Row("wake word", wakeBox);
            followUpBox = Check("after i answer out loud, keep listening a few seconds so you can reply", s.FollowUp);
            Row("", followUpBox);

            // --- nosy peeks
            nosyBox = Check("be nosy (peek at what i'm doing now and then)", s.BeNosy);
            Row("nosy", nosyBox);
            nosyMinutesBox = Num(5, 240, s.NosyMinutes, S(60));
            Row("", Lbl("about every", Ink), nosyMinutesBox, Lbl("minutes", Ink));
            speakNosyBox = Check("say nosy remarks out loud", s.SpeakNosy);
            Row("", speakNosyBox);
            skipBox = new TextBox();
            skipBox.Multiline = true;
            skipBox.AcceptsReturn = false;
            skipBox.WordWrap = true;
            skipBox.ScrollBars = ScrollBars.Vertical;
            skipBox.Width = S(420);
            skipBox.Height = S(58);
            skipBox.Font = small;
            skipBox.Text = s.NosySkipList ?? "";
            Row("", Stack(Note("never peek when the window or app mentions any of these (comma-separated):", Muted), skipBox));

            // --- browser
            browserStatus = Note(BrowserStatusText(), Muted);
            Row("browser", browserStatus);
            Row("", Btn("forget browser pairing", delegate
            {
                BrowserBridge b = BrowserBridge.Instance;
                if (b != null) b.ForgetPairing();
                browserStatus.Text = "forgot it. the next browser extension that says hi gets paired.";
            }));

            // --- google calendar
            gIdBox = new TextBox();
            gIdBox.Width = S(300);
            gIdBox.Text = s.GoogleClientId ?? "";
            gIdBox.HandleCreated += delegate { SendMessage(gIdBox.Handle, 0x1501, new IntPtr(1), "google oauth client id"); };
            var howTo = new LinkLabel();
            howTo.AutoSize = true;
            howTo.Font = small;
            howTo.Text = "how do i get these?";
            howTo.LinkColor = Warm;
            howTo.Margin = new Padding(S(6), S(6), 0, 0);
            howTo.LinkClicked += delegate { MessageBox.Show(this, AssistantCalendar.SetupSteps, "google calendar setup", MessageBoxButtons.OK, MessageBoxIcon.Information); };
            Row("calendar", gIdBox, howTo);
            gSecretBox = new TextBox();
            gSecretBox.Width = S(300);
            gSecretBox.UseSystemPasswordChar = true;
            bool secretSaved = s.GoogleClientSecret.Length > 0;
            gSecretBox.HandleCreated += delegate
            {
                SendMessage(gSecretBox.Handle, 0x1501, new IntPtr(1), secretSaved ? "client secret (saved; paste to replace)" : "client secret");
            };
            Row("", gSecretBox);
            connectButton = Btn("connect google calendar…", delegate { OnConnectCalendar(); });
            disconnectButton = Btn("disconnect", delegate { OnDisconnectCalendar(); });
            Row("", connectButton, disconnectButton);
            calStatus = Note(owner.Calendar.StatusText, Muted);
            Row("", calStatus);
            calWarnBox = Check("warn me before events", s.CalendarWarn);
            Row("", calWarnBox);
            RefreshCalendarButtons();

            // --- bottom row
            saveResult = Note("", Bad);
            var bottom = new FlowLayoutPanel();
            bottom.AutoSize = true;
            bottom.WrapContents = false;
            bottom.Margin = new Padding(0, S(12), 0, 0);
            bottom.Controls.Add(Btn("open my folder", delegate
            {
                try { AssistantTools.LaunchFolder(Home.Root); } catch (Exception ex) { DebugLog.Write("assistant: open folder: " + ex.Message); }
            }));
            var spacer = new Label();
            spacer.Width = S(60);
            bottom.Controls.Add(spacer);
            Button save = Btn("save", delegate { OnSave(); });
            save.Font = bold;
            bottom.Controls.Add(save);
            bottom.Controls.Add(Btn("cancel", delegate { Close(); }));
            table.Controls.Add(saveResult);
            table.SetColumnSpan(saveResult, 2);
            table.Controls.Add(bottom);
            table.SetColumnSpan(bottom, 2);
            AcceptButton = save;

            scroller.Controls.Add(table);
            Controls.Add(scroller);
            ResumeLayout(true);

            // fit the content, but never taller than the monitor (then it scrolls)
            Size pref = table.GetPreferredSize(Size.Empty);
            int maxH = (area.Height > 0 ? area.Height : Screen.PrimaryScreen.WorkingArea.Height) - S(40);
            int h = Math.Min(pref.Height + S(24), maxH);
            int w = pref.Width + S(28) + (pref.Height + S(24) > maxH ? SystemInformation.VerticalScrollBarWidth : 0);
            ClientSize = new Size(w, h);
            scroller.AutoScrollMargin = new Size(S(14), S(14));

            browserTimer = new System.Windows.Forms.Timer();
            browserTimer.Interval = 2000;
            browserTimer.Tick += delegate
            {
                if (!browserStatus.Text.StartsWith("forgot")) browserStatus.Text = BrowserStatusText();
                if (!calBusy) { calStatus.Text = owner.Calendar.StatusText; RefreshCalendarButtons(); }
            };
            FormClosed += delegate { owner.ApplyVoiceGain(); };   // undo an unsaved loudness preview
            browserTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { browserTimer.Dispose(); }
            base.Dispose(disposing);
            if (disposing) { font.Dispose(); bold.Dispose(); small.Dispose(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ little builders

        void Row(string label, params Control[] controls)
        {
            Label l = Lbl(label, Ink);
            l.Font = bold;
            l.Margin = new Padding(0, S(7), S(12), 0);
            Control content;
            if (controls.Length == 1) content = controls[0];
            else
            {
                var flow = new FlowLayoutPanel();
                flow.AutoSize = true;
                flow.WrapContents = false;
                flow.Margin = new Padding(0);
                foreach (Control c in controls) flow.Controls.Add(c);
                content = flow;
            }
            table.Controls.Add(l);
            table.Controls.Add(content);
        }

        Control Stack(params Control[] controls)
        {
            var flow = new FlowLayoutPanel();
            flow.AutoSize = true;
            flow.FlowDirection = FlowDirection.TopDown;
            flow.WrapContents = false;
            flow.Margin = new Padding(0);
            foreach (Control c in controls) flow.Controls.Add(c);
            return flow;
        }

        Label Lbl(string text, Color color) { return Lbl(text, color, 0); }

        Label Lbl(string text, Color color, int width)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.Font = font;
            l.Margin = new Padding(S(4), S(6), S(4), 0);
            if (width > 0) { l.AutoSize = false; l.Width = width; l.Height = S(24); }
            else l.AutoSize = true;
            return l;
        }

        Label Note(string text, Color color)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.Font = small;
            l.AutoSize = true;
            l.MaximumSize = new Size(S(420), 0);
            l.Margin = new Padding(S(2), S(2), 0, S(4));
            return l;
        }

        Button Btn(string text, EventHandler click)
        {
            var b = new Button();
            b.Text = text;
            b.Font = font;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(S(6), S(1), S(6), S(1));
            b.Margin = new Padding(S(4), S(2), S(4), S(2));
            b.UseVisualStyleBackColor = true;
            b.Click += click;
            return b;
        }

        ComboBox Combo(string[] items, string value, bool editable, int width)
        {
            var c = new ComboBox();
            c.DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            c.Width = width;
            c.Font = font;
            foreach (string i in items) c.Items.Add(i);
            if (editable) c.Text = value ?? "";
            return c;
        }

        CheckBox Check(string text, bool value)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Checked = value;
            c.AutoSize = true;
            c.Font = font;
            c.Margin = new Padding(S(2), S(5), 0, S(2));
            return c;
        }

        NumericUpDown Num(int min, int max, int value, int width)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = width;
            n.Font = font;
            return n;
        }

        // ------------------------------------------------------------------ status text

        string KeyHintText()
        {
            string src = s.KeySource;
            if (src == "saved") return "a key is saved (encrypted, only on this computer). paste a new one to replace it.";
            if (src == "env") return "using OPENAI_API_KEY from your environment. paste one here to save it instead.";
            return "paste your openai key (platform.openai.com → api keys). it stays on this computer, encrypted.";
        }

        void RefreshHotkeyStatus()
        {
            for (int i = 0; i < 3; i++)
            {
                string st = owner.HotkeyStatus[i] ?? "";
                hotkeyLabels[i].Text = st == "ok" ? "✓ works" : st == "off" ? "off" : st;
                hotkeyLabels[i].ForeColor = st == "ok" ? Good : st == "off" || st.Length == 0 ? Muted : Bad;
            }
        }

        internal static string ExtensionFolder()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                for (int i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
                {
                    string candidate = Path.Combine(dir, "extension");
                    if (Directory.Exists(candidate)) return candidate;
                    DirectoryInfo parent = Directory.GetParent(dir.TrimEnd('\\'));
                    dir = parent != null ? parent.FullName : null;
                }
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "extension");
            }
            catch { return "the extension folder"; }
        }

        internal static string BrowserStatusText()
        {
            BrowserBridge b = BrowserBridge.Instance;
            if (b != null && b.Connected) return "connected to " + (string.IsNullOrEmpty(b.BrowserName) ? "your browser" : b.BrowserName) + ".";
            return "not connected — load the extension from " + ExtensionFolder() +
                " (chrome://extensions → developer mode → load unpacked).";
        }

        // ------------------------------------------------------------------ actions

        void OnTest()
        {
            string key = AssistantSettings.CleanKey(keyBox.Text);
            if (key.Length == 0 && !keyCleared) key = s.ApiKey;
            if (key.Length == 0) { testResult.Text = "paste a key first."; testResult.ForeColor = Bad; return; }
            string baseUrl = baseUrlBox.Text.Trim();
            string model = modelBox.Text.Trim();
            if (model.Length == 0) model = AssistantSettings.DefaultModel;
            testButton.Enabled = false;
            testResult.ForeColor = Muted;
            testResult.Text = "asking the big outside…";
            var t = new Thread(delegate()
            {
                bool ok;
                string result = TestKey(baseUrl, key, model, out ok);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        testButton.Enabled = true;
                        testResult.Text = result;
                        testResult.ForeColor = ok ? Good : Bad;
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void OnTestAlexa()
        {
            string token = AssistantSettings.CleanKey(alexaTokenBox.Text);
            if (token.Length == 0) token = s.AlexaToken;
            if (token.Length == 0) { alexaStatus.Text = "paste your voice monkey token first. (\"how do i set this up?\" has the steps.)"; alexaStatus.ForeColor = Bad; return; }
            string device = alexaDeviceBox.Text.Trim().Length > 0 ? alexaDeviceBox.Text.Trim() : Alexa.DefaultDevice;
            string name = "bestie";
            try { name = owner.Host.CreatureName ?? name; } catch { }
            bool typed = alexaTokenBox.Text.Trim().Length > 0;
            alexaTestButton.Enabled = false;
            alexaStatus.ForeColor = Muted;
            alexaStatus.Text = "asking " + device + " to say hi…";
            var t = new Thread(delegate()
            {
                string problem = Alexa.Say(token, device, "hi, it's " + name + ". i can talk on this alexa now.");
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        alexaTestButton.Enabled = true;
                        alexaStatus.Text = problem.Length == 0 ? device + " should be talking now." + (typed ? " press save to keep it." : "") : problem;
                        alexaStatus.ForeColor = problem.Length == 0 ? Good : Bad;
                    });
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>One free call: GET /models/{model}. Checks the key and that the model exists.</summary>
        internal static string TestKey(string baseUrl, string key, string model, out bool ok)
        {
            ok = false;
            var c = new OpenAIClient(baseUrl, key);
            try
            {
                c.GetJson("models/" + Uri.EscapeDataString(model), 20000, model);
                ok = true;
                return "works. i can think now. (" + model + ")";
            }
            catch (OpenAIException ex)
            {
                if (ex.Status == 404) return "the key works, but the big outside doesn't know \"" + model + "\". try another model.";
                return ex.Friendly;
            }
            catch (Exception ex) { return "that didn't work: " + ex.Message; }
        }

        /// <summary>Settings as currently shown in the form. problem is non-null if something can't be saved.</summary>
        AssistantSettings Collect(out string problem)
        {
            problem = null;
            AssistantSettings n = s.Clone();
            string newKey = AssistantSettings.CleanKey(keyBox.Text);
            if (newKey.Length > 0) n.SetKey(newKey);
            else if (keyCleared) n.SetKey("");

            n.Model = modelBox.Text.Trim().Length > 0 ? modelBox.Text.Trim() : AssistantSettings.DefaultModel;
            n.ReasoningEffort = AssistantSettings.ReasoningChoices[Math.Max(0, effortBox.SelectedIndex)];
            n.TranscribeModel = transcribeBox.Text.Trim().Length > 0 ? transcribeBox.Text.Trim() : AssistantSettings.DefaultTranscribeModel;

            string url = baseUrlBox.Text.Trim().TrimEnd('/');
            if (url.Length == 0) url = AssistantSettings.DefaultBaseUrl;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                problem = "the api address should look like https://api.openai.com/v1";
            else n.BaseUrl = url;

            n.SpeakReplies = speakBox.Checked;
            n.VoiceEngine = engineBox.SelectedIndex == 1 ? "windows" : engineBox.SelectedIndex == 2 ? "elevenlabs" : "openai";
            string newEleven = AssistantSettings.CleanKey(elevenKeyBox.Text);
            if (newEleven.Length > 0) n.SetElevenKey(newEleven);
            int evi = elevenVoiceBox.SelectedIndex;
            if (evi >= 0 && evi < elevenVoiceIds.Count) { n.ElevenVoice = elevenVoiceIds[evi]; n.ElevenVoiceName = evi == 0 && n.ElevenVoice.Length == 0 ? "" : elevenVoiceBox.Text; }
            n.TranscribeEngine = elevenEarsBox.Checked ? "elevenlabs" : "openai";
            if ((n.VoiceEngine == "elevenlabs" || n.TranscribeEngine == "elevenlabs") && n.ElevenKey.Length == 0 && problem == null)
                problem = "elevenlabs needs its key: paste it in the elevenlabs box.";
            n.OpenAiVoice = openAiVoiceBox.Text.Length > 0 ? openAiVoiceBox.Text : AssistantSettings.DefaultOpenAiVoice;
            n.TtsModel = ttsModelBox.Text.Trim().Length > 0 ? ttsModelBox.Text.Trim() : AssistantSettings.DefaultTtsModel;
            n.TtsVoice = voiceBox.SelectedIndex <= 0 ? "" : voiceBox.Text;
            n.TtsRate = (int)rateBox.Value;
            string newAlexa = AssistantSettings.CleanKey(alexaTokenBox.Text);
            if (newAlexa.Length > 0) n.SetAlexaToken(newAlexa);
            n.AlexaDevice = alexaDeviceBox.Text.Trim().Length > 0 ? alexaDeviceBox.Text.Trim() : Alexa.DefaultDevice;
            n.AlexaWhen = alexaWhenBox.SelectedIndex == 1 ? "always" : alexaWhenBox.SelectedIndex == 2 ? "asked" : "away";

            string[] hk = new string[3];
            for (int i = 0; i < 3; i++)
            {
                hk[i] = hotkeyBoxes[i].Text.Trim();
                uint mods;
                Keys key;
                if (hk[i].Length > 0 && !HotkeyManager.TryParse(hk[i], out mods, out key) && problem == null)
                    problem = "i don't understand the hotkey \"" + hk[i] + "\". click the box and press the keys.";
            }
            n.HotkeyChat = hk[0];
            n.HotkeyVoice = hk[1];
            n.HotkeyScreen = hk[2];
            n.TalkKey = talkKeyBox.Text.Trim();
            n.AutoLookScreens = autoLookBox.Checked;
            n.AllowScreenTool = screenBox.Checked;
            n.AllowTabReading = tabsBox.Checked;
            n.MaxHistoryMessages = (int)historyBox.Value;
            n.BeNosy = nosyBox.Checked;
            n.NosyMinutes = (int)nosyMinutesBox.Value;
            n.SpeakNosy = speakNosyBox.Checked;
            n.NosySkipList = string.Join(", ", skipBox.Text.Split(',', '\n', '\r', ';')
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray());
            n.VoiceGainPercent = gainBar.Value;
            n.AutoMemory = autoMemoryBox.Checked;
            n.WakeWord = wakeBox.Checked;
            n.FollowUp = followUpBox.Checked;
            n.GoogleClientId = gIdBox.Text.Trim();
            if (gSecretBox.Text.Trim().Length > 0) n.SetGoogleClientSecret(gSecretBox.Text);
            n.CalendarWarn = calWarnBox.Checked;
            return n;
        }

        void RefreshCalendarButtons()
        {
            bool connected = owner.Calendar.IsConnected;
            bool busy = calBusy || owner.Calendar.Connecting;
            connectButton.Enabled = !busy;
            connectButton.Text = connected ? "reconnect…" : "connect google calendar…";
            disconnectButton.Enabled = connected && !busy;
        }

        void OnConnectCalendar()
        {
            string id = gIdBox.Text.Trim();
            string secret = gSecretBox.Text.Trim().Length > 0 ? gSecretBox.Text.Trim() : s.GoogleClientSecret;
            if (id.Length == 0)
            {
                calStatus.Text = "paste the client id first. (\"how do i get these?\" has the steps.)";
                calStatus.ForeColor = Bad;
                return;
            }
            calBusy = true;
            calStatus.ForeColor = Muted;
            calStatus.Text = "opening your browser… say yes there. (i'll wait 3 minutes.)";
            RefreshCalendarButtons();
            owner.Calendar.Connect(id, secret, delegate(bool ok, string message)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        calBusy = false;
                        if (ok) owner.RememberGoogleClient(id, secret);
                        if (IsDisposed) return;
                        calStatus.Text = ok ? owner.Calendar.StatusText + " bestie has your calendar now." : message;
                        calStatus.ForeColor = ok ? Good : Bad;
                        RefreshCalendarButtons();
                    });
                }
                catch { calBusy = false; }
            });
        }

        void OnDisconnectCalendar()
        {
            calBusy = true;
            calStatus.ForeColor = Muted;
            calStatus.Text = "disconnecting…";
            RefreshCalendarButtons();
            owner.Calendar.Disconnect(delegate(string message)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        calBusy = false;
                        if (IsDisposed) return;
                        calStatus.Text = message;
                        RefreshCalendarButtons();
                    });
                }
                catch { calBusy = false; }
            });
        }

        /// <summary>Fills the elevenlabs voice list from their account (the key in the box, else the saved one).</summary>
        void OnLoadElevenVoices()
        {
            string key = AssistantSettings.CleanKey(elevenKeyBox.Text);
            if (key.Length == 0) key = s.ElevenKey;
            if (key.Length == 0) { saveResult.Text = "paste your elevenlabs key first."; return; }
            saveResult.Text = "asking elevenlabs for your voices…";
            string keep = elevenVoiceIds.Count > elevenVoiceBox.SelectedIndex && elevenVoiceBox.SelectedIndex >= 0 ? elevenVoiceIds[elevenVoiceBox.SelectedIndex] : "";
            var t = new System.Threading.Thread(delegate()
            {
                List<KeyValuePair<string, string>> voices = null; string err = null;
                try { voices = ElevenLabs.Voices(key); } catch (OpenAIException ex) { err = ex.Friendly; } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (voices == null) { saveResult.Text = err; return; }
                        elevenVoiceBox.Items.Clear(); elevenVoiceIds.Clear();
                        foreach (var v in voices) { elevenVoiceBox.Items.Add(v.Value); elevenVoiceIds.Add(v.Key); }
                        int i = Math.Max(0, elevenVoiceIds.IndexOf(keep));
                        if (elevenVoiceBox.Items.Count > 0) elevenVoiceBox.SelectedIndex = i;
                        saveResult.Text = voices.Count + " voices. pick one, press try, then save.";
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void OnTryVoice()
        {
            string problem;
            AssistantSettings n = Collect(out problem);
            if (n.VoiceEngine == "openai" && !n.HasKey)
                saveResult.Text = "the openai voice needs a key, so here's the windows one.";
            if (n.VoiceEngine == "elevenlabs" && n.ElevenKey.Length == 0)
                saveResult.Text = "the elevenlabs voice needs its key.";
            owner.PreviewVoice(n);
        }

        void OnSave()
        {
            string problem;
            AssistantSettings n = Collect(out problem);
            if (problem != null) { saveResult.Text = problem; return; }

            owner.ApplySettings(n);
            RefreshHotkeyStatus();
            bool hotkeysOk = true;
            for (int i = 0; i < 3; i++) if (owner.HotkeyStatus[i] != "ok" && owner.HotkeyStatus[i] != "off") hotkeysOk = false;
            if (!hotkeysOk)
            {
                saveResult.Text = "saved, but a hotkey didn't work (see above). pick a different combo?";
                return;
            }
            Close();
        }
    }
}
