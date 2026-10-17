using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;
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
        MicRing ring;            // the last few seconds of mic, so a guess can be checked before the sentence is over
        long speechAt = -1;      // where in it the current sentence started
        bool guessed;            // this sentence already got its early check
        string[] names = new string[0];
        double lastLevelLog;
        int maxLevel, detected, rejected, ordinary, accepted;

        /// <summary>Tests: listen to this WAV file instead of the microphone.</summary>
#pragma warning disable 649   // only tests set it
        internal static string TestWavePath;
#pragma warning restore 649
        /// <summary>Tests: also load ordinary dictation as a competitor. Off: with it, real wake phrases get written
        /// out as ordinary words ("hey bestie" -> "a best you") and it stops hearing you.</summary>
        internal static bool UseDictation = false;
        // how sure the recognizer must be that the word it heard was its name. kept low on purpose: false alarms
        // score just as high as real ones (0.6-0.95), so this only decides sensitivity. the transcript check decides.
        const float NameConfidence = 0.4f, NearMissConfidence = 0.42f;

        /// <summary>Wake phrase plus (maybe) a command. Args: WAV of the whole utterance, the text SAPI heard. UI thread.</summary>
        public event Action<byte[], string> Heard;
        /// <summary>It thinks it just heard its name, while they're still talking: a WAV of the sentence so far, to check
        /// right away (Heard comes only once the whole sentence is over). Once per sentence. UI thread.</summary>
        public event Action<byte[]> Perked;
        /// <summary>Set by the app: results are ignored while this returns true (e.g. it's talking or already recording).</summary>
        public Func<bool> Busy;

        public bool Running { get { return engine != null; } }
        public string[] Names { get { return names; } }
        /// <summary>Something's playing (a video or music keeps "saying" its name): see Noisy.</summary>
        public bool NoisyNow { get { return Noisy(); } }

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
                if (TestWavePath != null) engine.SetInputToWaveFile(TestWavePath); else engine.SetInputToDefaultAudioDevice();

                var greet = new Choices(Greetings);
                var who = new Choices(names);

                var withCommand = new GrammarBuilder(greet) { Culture = info.Culture };
                withCommand.Append(who);
                withCommand.AppendWildcard();
                var bare = new GrammarBuilder(greet) { Culture = info.Culture };
                bare.Append(who);
                engine.LoadGrammar(new Grammar(withCommand) { Name = "command" });
                engine.LoadGrammar(new Grammar(bare) { Name = "bare" });
                // just the name works too: "blorp, open youtube" / "bestie, what's next on my calendar". (noise gets heard as a
                // bare name a lot; that's fine: without these, the same noise lands on "hey <name>" instead.)
                var nameOnly = new GrammarBuilder(who) { Culture = info.Culture };
                nameOnly.AppendWildcard();
                engine.LoadGrammar(new Grammar(nameOnly) { Name = "name" });
                var nameBare = new GrammarBuilder(who) { Culture = info.Culture };
                engine.LoadGrammar(new Grammar(nameBare) { Name = "name-bare" });
                // ordinary speech competes with the wake phrases. without it, every sound (music, a video, people
                // talking) gets squeezed into the nearest wake phrase, often at 90%+ confidence.
                if (UseDictation)
                {
                    try { engine.LoadGrammar(new DictationGrammar { Name = "dictation" }); }
                    catch (Exception ex) { DebugLog.Write("wake word: no dictation to compare against (" + ex.Message + ")"); }
                }

                // let people pause mid-sentence without being cut off
                engine.EndSilenceTimeout = TimeSpan.FromSeconds(0.9);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromSeconds(1.3);
                engine.BabbleTimeout = TimeSpan.Zero;

                engine.SpeechRecognized += OnRecognized;
                engine.SpeechHypothesized += OnHypothesized;
                engine.SpeechDetected += (s, e) =>
                {
                    detected++;
                    guessed = false;
                    if (ring != null) speechAt = Math.Max(0, ring.Position - MicRing.Bytes(0.7));   // the recognizer notices a beat late
                };
                engine.SpeechRecognitionRejected += OnRejected;
                engine.AudioLevelUpdated += OnLevel;
                if (TestWavePath == null)
                {
                    ring = new MicRing();
                    if (!ring.Start()) ring = null;
                }
                engine.RecognizeAsync(RecognizeMode.Multiple);
                DebugLog.Write("wake word: listening for \"hey " + string.Join("\" / \"hey ", names) + "\" on " + info.Culture.Name + (ring == null ? " (no early check)" : ""));
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
            if (ring != null) { ring.Dispose(); ring = null; }
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
                DebugLog.Write("wake word: mic loudest " + maxLevel + "/100, speech bits heard " + detected + ", ordinary talk " + ordinary + ", unclear " + rejected + ", for me " + accepted);
                maxLevel = 0;
            }
        }

        void OnRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            rejected++;
            guessed = false;
            var r = e.Result;
            if (r == null || r.Alternates.Count == 0) return;
            var best = r.Alternates[0];
            // quiet mics make the recognizer shy: a near miss that clearly starts with a greeting + its name still counts
            var words = best.Words.Select(w => w.Text.ToLowerInvariant()).ToList();
            int at = words.FindIndex(w => names.Contains(w));
            bool upFront = at == 0 || (at == 1 && Greetings.Contains(words[0]));
            if (upFront && best.Confidence >= NearMissConfidence && (best.Grammar == null || best.Grammar.Name != "dictation"))
            {
                byte[] wav = null;
                try { if (r.Audio != null) using (var ms = new MemoryStream()) { r.Audio.WriteToWaveStream(ms); wav = ms.ToArray(); } } catch { }
                if (wav != null)
                {
                    string text = best.Text;
                    accepted++;
                    Guessed();
                    if (OnlyHeyNow(text, best.Confidence)) return;
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
            guessed = false;
            var r = e.Result;
            if (r == null || r.Words.Count < 1) return;
            float wakeConfidence;
            if (r.Grammar != null && r.Grammar.Name == "dictation")
            {
                // ordinary talk. it only counts if it starts with its name ("bestie, open youtube" / "hey bessie ...")
                if (!StartsWithName(r.Text)) { ordinary++; return; }
                wakeConfidence = r.Confidence;
            }
            else
            {
                // the name has to be heard clearly; the command part can be anything
                var nameWord = r.Words.FirstOrDefault(w => names.Contains(w.Text.ToLowerInvariant()));
                wakeConfidence = nameWord != null ? nameWord.Confidence : 0;
            }
            if (wakeConfidence < NameConfidence) { rejected++; return; }
            accepted++;
            Guessed();
            if (OnlyHeyNow(r.Text, wakeConfidence)) return;
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

        /// <summary>Its name (or something the transcriber writes for it) as the first or second word.</summary>
        bool StartsWithName(string text)
        {
            string[] first = (text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2).ToArray();
            return first.Length > 0 && AssistantModule.SaysName(string.Join(" ", first), Phrases());
        }

        void OnHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            var r = e.Result;
            MicRing mic = ring;
            // only a confident "hey <name>" (ordinary talk mentioning it doesn't), once a sentence. a bare name doesn't count here:
            // noise gets heard as "blorp" hundreds of times a day, and each early check is a paid transcription
            if (r == null || r.Confidence < 0.6f || guessed || mic == null) return;
            string[] words = (r.Text ?? "").ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || !Greetings.Contains(words[0]) || !StartsWithName(r.Text)) return;
            if (Noisy()) return;   // a video or music keeps "saying" its name: the plain (slower) glow will do
            guessed = true;
            long from = Math.Max(speechAt, mic.Position - MicRing.Bytes(3.5));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(350);   // the end of its name may still be on its way in
                byte[] wav = mic.WavSince(from);
                Post(() =>
                {
                    if (Busy != null && Busy()) return;
                    if (Perked != null) Perked(wav);
                });
            });
        }

        // when it last thought it heard its name; almost all of these are noise (a video, music, a call)
        readonly Queue<double> recentGuesses = new Queue<double>();

        void Guessed()
        {
            lock (recentGuesses) recentGuesses.Enqueue(Environment.TickCount / 1000.0);
        }

        /// <summary>While something's playing, a bare name ("blorp", "bestie ...") isn't checked: a video says something like
        /// it every few seconds and each check is a paid transcription. "hey &lt;name&gt;" still is. True = drop this guess.</summary>
        bool OnlyHeyNow(string text, float confidence)
        {
            string[] words = (text ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if ((words.Length >= 2 && Greetings.Contains(words[0])) || !Noisy()) return false;
            DebugLog.Write("wake word: heard \"" + text + "\" (" + confidence.ToString("0.00") + "); something's playing, so only \"hey " + (names.Length > 0 ? names[0] : "bestie") + "\" counts");
            return true;
        }

        /// <summary>Three or more name guesses in the last three minutes: something's playing, so no early checks (each is a paid transcription).</summary>
        bool Noisy()
        {
            double now = Environment.TickCount / 1000.0;
            lock (recentGuesses)
            {
                while (recentGuesses.Count > 0 && now - recentGuesses.Peek() > 180) recentGuesses.Dequeue();
                return recentGuesses.Count >= 3;
            }
        }

        void Post(Action a)
        {
            try { if (ui.IsHandleCreated && !ui.IsDisposed) ui.BeginInvoke(a); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
