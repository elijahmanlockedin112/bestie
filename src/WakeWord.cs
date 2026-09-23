using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Recognition;
using System.Windows.Forms;

namespace Bestie
{
    /// <summary>Listens (offline, with the Windows speech recognizer) for "hi/hey &lt;name&gt; …" for any of its names.
    /// Nothing leaves the computer until the wake phrase is heard; then the whole utterance's audio is handed over
    /// so the assistant can transcribe it and act on it.</summary>
    public sealed class WakeWord : IDisposable
    {
        readonly Control ui;
        SpeechRecognitionEngine engine;
        string[] names = new string[0];
        double lastPerk, lastLevelLog;
        int maxLevel, detected, rejected;

        /// <summary>Wake phrase plus (maybe) a command. Args: WAV of the whole utterance, the text SAPI heard. UI thread.</summary>
        public event Action<byte[], string> Heard;
        /// <summary>It thinks it just heard its name mid-sentence (for a little "!" reaction). UI thread.</summary>
        public event Action Perked;
        /// <summary>Set by the app: results are ignored while this returns true (e.g. it's talking or already recording).</summary>
        public Func<bool> Busy;

        public bool Running { get { return engine != null; } }
        public string[] Names { get { return names; } }

        public static readonly string[] Greetings = { "hi", "hey", "hello", "okay", "yo", "ok" };

        public WakeWord(Control ui) { this.ui = ui; }

        public string[] Phrases()
        {
            return Greetings.SelectMany(g => names.Select(n => g + " " + n)).ToArray();
        }

        public bool Start(IEnumerable<string> wakeNames)
        {
            if (engine != null) return true;
            names = wakeNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToLowerInvariant()).Distinct().ToArray();
            if (names.Length == 0) return false;
            try
            {
                var info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.Name == "en-US")
                        ?? SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
                if (info == null) { DebugLog.Write("wake word: no english speech recognizer installed"); return false; }
                engine = new SpeechRecognitionEngine(info);
                engine.SetInputToDefaultAudioDevice();

                var greet = new Choices(Greetings);
                var who = new Choices(names);

                var withCommand = new GrammarBuilder(greet) { Culture = info.Culture };
                withCommand.Append(who);
                withCommand.AppendWildcard();
                var bare = new GrammarBuilder(greet) { Culture = info.Culture };
                bare.Append(who);
                engine.LoadGrammar(new Grammar(withCommand) { Name = "command" });
                engine.LoadGrammar(new Grammar(bare) { Name = "bare" });
                // just the name works too: "blorp, open youtube" / "bestie, what's next on my calendar"
                var nameOnly = new GrammarBuilder(who) { Culture = info.Culture };
                nameOnly.AppendWildcard();
                engine.LoadGrammar(new Grammar(nameOnly) { Name = "name" });
                var nameBare = new GrammarBuilder(who) { Culture = info.Culture };
                engine.LoadGrammar(new Grammar(nameBare) { Name = "name-bare" });

                // let people pause mid-sentence without being cut off
                engine.EndSilenceTimeout = TimeSpan.FromSeconds(0.9);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.3);
                engine.BabbleTimeout = TimeSpan.Zero;

                engine.SpeechRecognized += OnRecognized;
                engine.SpeechHypothesized += OnHypothesized;
                engine.SpeechDetected += (s, e) => detected++;
                engine.SpeechRecognitionRejected += OnRejected;
                engine.AudioLevelUpdated += OnLevel;
                engine.RecognizeAsync(RecognizeMode.Multiple);
                DebugLog.Write("wake word: listening for \"hey " + string.Join("\" / \"hey ", names) + "\" on " + info.Culture.Name);
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Write("wake word: couldn't start (" + ex.Message + ")");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            var e = engine;
            engine = null;
            if (e == null) return;
            try { e.RecognizeAsyncCancel(); } catch { }
            try { e.Dispose(); } catch { }
        }

        void OnLevel(object sender, AudioLevelUpdatedEventArgs e)
        {
            if (e.AudioLevel > maxLevel) maxLevel = e.AudioLevel;
            // a heartbeat in the log every couple of minutes, so "it can't hear me" is easy to diagnose
            double now = Environment.TickCount / 1000.0;
            if (now - lastLevelLog > 120)
            {
                lastLevelLog = now;
                DebugLog.Write("wake word: mic loudest " + maxLevel + "/100, speech bits heard " + detected + ", not-a-wake-word " + rejected);
                maxLevel = 0;
            }
        }

        void OnRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            rejected++;
            var r = e.Result;
            if (r == null || r.Alternates.Count == 0) return;
            var best = r.Alternates[0];
            // quiet mics make the recognizer shy: a near miss that clearly starts with a greeting + its name still counts
            var words = best.Words.Select(w => w.Text.ToLowerInvariant()).ToList();
            int at = words.FindIndex(w => names.Contains(w));
            bool upFront = at == 0 || (at == 1 && Greetings.Contains(words[0]));
            if (upFront && best.Confidence >= 0.42f)
            {
                byte[] wav = null;
                try { if (r.Audio != null) using (var ms = new MemoryStream()) { r.Audio.WriteToWaveStream(ms); wav = ms.ToArray(); } } catch { }
                if (wav != null)
                {
                    string text = best.Text;
                    DebugLog.Write("wake word: heard (near miss, " + best.Confidence.ToString("0.00") + ") \"" + text + "\"");
                    Post(() =>
                    {
                        if (Busy != null && Busy()) return;
                        if (Heard != null) Heard(wav, text);
                    });
                    return;
                }
            }
            // log other near misses to help tune it
            if (best.Confidence > 0.25f && at >= 0)
                DebugLog.Write("wake word: almost (" + best.Text + ", " + best.Confidence.ToString("0.00") + ")");
        }

        void OnRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            var r = e.Result;
            if (r == null || r.Words.Count < 1) return;
            // the name has to be heard clearly; the command part can be anything
            var nameWord = r.Words.FirstOrDefault(w => names.Contains(w.Text.ToLowerInvariant()));
            float wakeConfidence = nameWord != null ? nameWord.Confidence : 0;
            if (wakeConfidence < 0.4f) { DebugLog.Write("wake word: ignored a maybe (" + r.Text + ", " + wakeConfidence.ToString("0.00") + ")"); return; }
            byte[] wav = null;
            try
            {
                if (r.Audio != null)
                    using (var ms = new MemoryStream()) { r.Audio.WriteToWaveStream(ms); wav = ms.ToArray(); }
            }
            catch (Exception ex) { DebugLog.Write("wake word: audio: " + ex.Message); }
            string text = r.Text;
            DebugLog.Write("wake word: heard \"" + text + "\" (" + wakeConfidence.ToString("0.00") + ")");
            Post(() =>
            {
                if (Busy != null && Busy()) { DebugLog.Write("wake word: busy, ignoring"); return; }
                if (Heard != null) Heard(wav, text);
            });
        }

        void OnHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            var r = e.Result;
            if (r == null || r.Confidence < 0.35f || !r.Words.Any(w => names.Contains(w.Text.ToLowerInvariant()))) return;
            Post(() =>
            {
                double now = Clock.T;
                if (now - lastPerk < 0.5 || (Busy != null && Busy())) return;
                lastPerk = now;
                if (Perked != null) Perked();
            });
        }

        void Post(Action a)
        {
            try { if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
