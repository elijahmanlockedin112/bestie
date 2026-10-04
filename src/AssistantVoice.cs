// AssistantVoice.cs - push-to-talk recording (waveIn, 16 kHz 16-bit mono), a tiny VAD,
// in-memory WAV, and local text-to-speech (System.Speech). C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;

namespace Bestie
{
    internal static class VoiceNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        public const uint WAVE_MAPPER = 0xFFFFFFFF;
        public const uint CALLBACK_NULL = 0;
        public const int WHDR_DONE = 1;

        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr hwi);

        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern int waveOutSetVolume(IntPtr hwo, uint volume);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);
    }

    /// <summary>Plays 16-bit mono PCM from a stream through waveOut while it downloads.</summary>
    internal static class PcmStreamPlayer
    {
        /// <summary>How much louder than OpenAI's (fairly quiet) output to play the voice. Peaks are soft-limited, not clipped.</summary>
        public static double Gain = 2.4;

        /// <summary>Boost 16-bit samples in place with a soft knee above ~70% so loud bits round off instead of crackling.</summary>
        static void Amplify(byte[] data, int count)
        {
            if (Gain <= 1.0) return;
            const double knee = 0.7;
            for (int i = 0; i + 1 < count; i += 2)
            {
                double x = (short)(data[i] | (data[i + 1] << 8)) / 32768.0 * Gain;
                double a = Math.Abs(x);
                if (a > knee) a = knee + (1 - knee) * Math.Tanh((a - knee) / (1 - knee));
                int s = (int)Math.Round(Math.Sign(x) * a * 32767);
                data[i] = (byte)(s & 0xFF);
                data[i + 1] = (byte)((s >> 8) & 0xFF);
            }
        }

        /// <summary>Blocks until the audio has played, the stream ends, or cancelled() turns true.
        /// firstAudio runs once, right when sound starts. Returns bytes queued for playback.</summary>
        public static long Play(Stream src, int sampleRate, Func<bool> cancelled, Action firstAudio)
        {
            bool given = false;
            return PlaySequence(delegate { if (given) return null; given = true; return src; }, sampleRate, cancelled, firstAudio);
        }

        /// <summary>Plays several PCM streams back to back through ONE waveOut session (no gap from reopening the
        /// device). next() blocks until the next stream is ready (e.g. the next sentence's audio) and returns null
        /// when there are no more. Each stream is played while it downloads.</summary>
        public static long PlaySequence(Func<Stream> next, int sampleRate, Func<bool> cancelled, Action firstAudio)
        {
            if (VoiceNative.waveOutGetNumDevs() <= 0) throw new InvalidOperationException("no speakers found");
            var fmt = new VoiceNative.WAVEFORMATEX();
            fmt.wFormatTag = 1;
            fmt.nChannels = 1;
            fmt.nSamplesPerSec = (uint)sampleRate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = 2;
            fmt.nAvgBytesPerSec = (uint)(sampleRate * 2);
            IntPtr h;
            int rc = VoiceNative.waveOutOpen(out h, VoiceNative.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, VoiceNative.CALLBACK_NULL);
            if (rc != 0) throw new InvalidOperationException("couldn't open the speakers (error " + rc + ")");
            VoiceNative.waveOutSetVolume(h, 0xFFFFFFFF);   // our stream at full volume (the system volume still applies)
            int hdrSize = Marshal.SizeOf(typeof(VoiceNative.WAVEHDR));
            int flagsOffset = Marshal.OffsetOf(typeof(VoiceNative.WAVEHDR), "dwFlags").ToInt32();
            var pending = new List<IntPtr[]>();     // { header, data }
            int chunk = sampleRate * 2 / 4;         // 250 ms per buffer
            int prebuffer = sampleRate * 2 * 3 / 10; // wait for 300 ms before starting (avoids stutter; tts arrives faster than realtime)
            var acc = new MemoryStream();
            var buf = new byte[16384];
            long written = 0;
            bool started = false;
            Action<byte[], int> write = delegate(byte[] data, int count)
            {
                Amplify(data, count);
                IntPtr mem = Marshal.AllocHGlobal(count);
                IntPtr hdr = Marshal.AllocHGlobal(hdrSize);
                Marshal.Copy(data, 0, mem, count);
                var wh = new VoiceNative.WAVEHDR();
                wh.lpData = mem;
                wh.dwBufferLength = (uint)count;
                Marshal.StructureToPtr(wh, hdr, false);
                pending.Add(new[] { hdr, mem });
                if (VoiceNative.waveOutPrepareHeader(h, hdr, (uint)hdrSize) != 0 || VoiceNative.waveOutWrite(h, hdr, (uint)hdrSize) != 0)
                    throw new InvalidOperationException("the speakers refused the audio");
                written += count;
                if (!started) { started = true; if (firstAudio != null) firstAudio(); }
            };
            Action flush = delegate
            {
                byte[] all = acc.ToArray();
                int take = all.Length & ~1;
                if (take > 0)
                {
                    var part = new byte[take];
                    Array.Copy(all, part, take);
                    write(part, take);
                }
                acc = new MemoryStream();
                if (all.Length > take) acc.Write(all, take, all.Length - take);
            };
            Action reap = delegate
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    IntPtr[] p = pending[i];
                    if ((Marshal.ReadInt32(p[0], flagsOffset) & VoiceNative.WHDR_DONE) == 0) continue;
                    VoiceNative.waveOutUnprepareHeader(h, p[0], (uint)hdrSize);
                    Marshal.FreeHGlobal(p[0]);
                    Marshal.FreeHGlobal(p[1]);
                    pending.RemoveAt(i);
                }
            };
            try
            {
                while (!cancelled())
                {
                    Stream src = next();
                    if (src == null) break;
                    while (!cancelled())
                    {
                        int n;
                        try { n = src.Read(buf, 0, buf.Length); }
                        catch { if (cancelled()) break; throw; }
                        if (n <= 0) break;
                        acc.Write(buf, 0, n);
                        if (acc.Length >= (started ? chunk : prebuffer)) flush();
                        reap();
                    }
                    if (!cancelled() && acc.Length >= 2) flush();   // end of a sentence: play its tail now (and start, if it was short)
                    reap();
                }
                if (!cancelled() && acc.Length >= 2) flush();
                while (!cancelled() && pending.Count > 0) { reap(); Thread.Sleep(25); }
            }
            finally
            {
                if (pending.Count > 0) VoiceNative.waveOutReset(h);   // cancelled: cut it off now
                foreach (IntPtr[] p in pending)
                {
                    VoiceNative.waveOutUnprepareHeader(h, p[0], (uint)hdrSize);
                    Marshal.FreeHGlobal(p[0]);
                    Marshal.FreeHGlobal(p[1]);
                }
                VoiceNative.waveOutClose(h);
            }
            return written;
        }
    }

    /// <summary>Speaks replies: OpenAI text-to-speech (steered with Personality.VoiceDirection) when chosen and a
    /// key exists, else the free windows voice. An utterance is Begin, Add (one or more sentences, e.g. while a
    /// reply is still streaming in), End. OpenAI sentences are fetched one after another and played through one
    /// continuous waveOut session, each while it downloads. Any OpenAI failure before sound starts switches the
    /// utterance to the windows voice (logged). A new Begin/Speak, Stop or Dispose cuts off the current one.
    /// All methods may be called from any thread. Started/Finished may be raised on any thread.</summary>
    internal sealed class AssistantSpeaker : IDisposable
    {
        public const int PcmRate = 24000;   // /audio/speech "pcm" = 24 kHz 16-bit mono little-endian

        /// <summary>Tests: replaces the windows voice (gets the text instead of speaking it).</summary>
#pragma warning disable 649   // only tests set it
        internal static Action<string> TestWindowsVoiceHook;
#pragma warning restore 649

        sealed class Utterance
        {
            public int Gen;
            public AssistantSettings S;
            public OpenAIClient Client;
            public bool Eleven;                         // speaking through elevenlabs
            public volatile HttpWebRequest Req;         // its request in flight (so Stop can cut it off)
            public string Prev;                         // the last sentence it said (elevenlabs keeps the tone flowing)
            public readonly Queue<string> Pending = new Queue<string>();
            public readonly List<string> All = new List<string>();
            public bool Ended, Windows, WindowsBegun;
            public volatile bool Played;
        }

        readonly VoiceTts windows = new VoiceTts();   // UI thread only (reached through postToUi)
        readonly Action<Action> postToUi;
        readonly object gate = new object();
        int generation;
        Utterance cur;
        Utterance windowsOwner;                       // UI thread
        volatile bool disposed;

        public event Action Started;
        public event Action Finished;

        public AssistantSpeaker(Action<Action> postToUi)
        {
            this.postToUi = postToUi;
            windows.Started += delegate { if (IsCurrent(windowsOwner)) Raise(Started); };
            windows.Finished += delegate
            {
                Utterance u = windowsOwner;
                windowsOwner = null;
                if (u != null && FinishIfCurrent(u)) Raise(Finished);
            };
        }

        public bool Speaking { get { lock (gate) return cur != null; } }

        void Raise(Action a)
        {
            if (a == null) return;
            try { a(); } catch (Exception ex) { DebugLog.Write("assistant: speaker event: " + ex.Message); }
        }

        bool IsCurrent(Utterance u) { lock (gate) return u != null && cur == u && !disposed; }

        bool FinishIfCurrent(Utterance u)
        {
            lock (gate)
            {
                if (cur != u) return false;
                cur = null;
                return true;
            }
        }

        /// <summary>Speak one complete text (cuts off whatever is playing).</summary>
        public void Speak(string text, AssistantSettings s)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) { Stop(); return; }
            Begin(s);
            Add(text);
            End();
        }

        /// <summary>Start a new utterance (cuts off whatever is playing). The engine is chosen now.</summary>
        public void Begin(AssistantSettings s)
        {
            if (disposed) return;
            Stop();
            var u = new Utterance();
            u.S = s.Clone();
            u.Eleven = s.UseElevenVoice;
            u.Windows = !u.Eleven && !s.UseOpenAiVoice;
            lock (gate)
            {
                u.Gen = ++generation;
                cur = u;
            }
            if (u.Windows) return;   // the windows voice starts with the first sentence (on the UI thread)
            if (!u.Eleven) u.Client = new OpenAIClient(s.BaseUrl, s.ApiKey);
            var t = new Thread(delegate() { RunOpenAi(u); });
            t.IsBackground = true;
            t.Name = "bestie-voice";
            t.Start();
        }

        /// <summary>Queue a sentence of the current utterance.</summary>
        public void Add(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            lock (gate)
            {
                Utterance u = cur;
                if (u == null) return;
                u.All.Add(text);
                if (u.Windows) postToUi(delegate { WindowsSay(u, text); });
                else { u.Pending.Enqueue(text); Monitor.PulseAll(gate); }
            }
        }

        /// <summary>No more sentences for the current utterance.</summary>
        public void End()
        {
            lock (gate)
            {
                Utterance u = cur;
                if (u == null) return;
                u.Ended = true;
                if (u.Windows) postToUi(delegate { WindowsEnd(u); });
                else Monitor.PulseAll(gate);
            }
        }

        // ---------------------------------------------------------------- windows voice (UI thread)

        void WindowsSay(Utterance u, string text)
        {
            if (!IsCurrent(u)) return;
            Action<string> hook = TestWindowsVoiceHook;
            if (!u.WindowsBegun)
            {
                u.WindowsBegun = true;
                windowsOwner = u;
                if (hook != null) Raise(Started);
                else windows.Begin(u.S.TtsVoice, u.S.TtsRate);
            }
            if (hook != null) hook(text);
            else windows.Enqueue(text);
        }

        void WindowsEnd(Utterance u)
        {
            if (!IsCurrent(u)) return;
            if (!u.WindowsBegun || TestWindowsVoiceHook != null)
            {
                if (u.WindowsBegun) windowsOwner = null;
                if (FinishIfCurrent(u)) Raise(Finished);   // nothing was said, or the test hook "said" it
                return;
            }
            windows.End();
        }

        // ---------------------------------------------------------------- openai voice (its own thread)

        void RunOpenAi(Utterance u)
        {
            string model = string.IsNullOrEmpty(u.S.TtsModel) ? AssistantSettings.DefaultTtsModel : u.S.TtsModel;
            string voice = string.IsNullOrEmpty(u.S.OpenAiVoice) ? AssistantSettings.DefaultOpenAiVoice : u.S.OpenAiVoice;
            HttpWebResponse resp = null;
            string fail = null;
            Func<bool> stale = delegate { return !IsCurrent(u); };
            Func<Stream> next = delegate
            {
                if (resp != null) { try { resp.Close(); } catch { } resp = null; }
                string text;
                lock (gate)
                {
                    while (cur == u && u.Pending.Count == 0 && !u.Ended) Monitor.Wait(gate, 250);
                    if (cur != u || u.Pending.Count == 0) return null;
                    text = u.Pending.Dequeue();
                }
                try { resp = u.Eleven ? OpenEleven(u, text) : OpenSpeech(u.Client, model, voice, text); }
                catch (OpenAIException ex)
                {
                    if (ex.Aborted) return null;
                    if (!u.Played) throw;   // nothing heard yet: the whole thing goes to the windows voice
                    DebugLog.Write("assistant: openai voice skipped a sentence: " + ex.Message);
                    return new MemoryStream(new byte[0]);
                }
                return resp.GetResponseStream();
            };
            try
            {
                PcmStreamPlayer.PlaySequence(next, PcmRate, stale, delegate
                {
                    u.Played = true;
                    if (!stale()) Raise(Started);
                });
            }
            catch (OpenAIException ex) { if (!ex.Aborted) fail = ex.Message; }
            catch (Exception ex) { if (!stale()) fail = ex.GetType().Name + ": " + ex.Message; }
            finally { if (resp != null) { try { resp.Close(); } catch { } } }
            if (stale()) return;   // superseded or stopped: Stop() already said Finished
            if (fail != null && !u.Played)
            {
                DebugLog.Write("assistant: " + (u.Eleven ? "elevenlabs" : "openai") + " voice failed (" + fail + "); using the windows voice");
                lock (gate)
                {
                    if (cur != u) return;
                    u.Windows = true;
                    string[] all = u.All.ToArray();
                    bool ended = u.Ended;
                    postToUi(delegate
                    {
                        foreach (string t in all) WindowsSay(u, t);
                        if (ended) WindowsEnd(u);
                    });
                }
                return;
            }
            if (fail != null) DebugLog.Write("assistant: openai voice broke mid-sentence: " + fail);
            if (FinishIfCurrent(u)) Raise(Finished);
        }

        static HttpWebResponse OpenEleven(Utterance u, string text)
        {
            string key = u.S.ElevenKey;
            string voice = ElevenLabs.VoiceOrDefault(key, u.S.ElevenVoice);
            HttpWebResponse r = ElevenLabs.Speak(key, voice, u.S.ElevenModel, text, u.Prev, 30000, req => u.Req = req);
            u.Prev = text;
            return r;
        }

        /// <summary>With Personality.VoiceDirection as instructions; if the model rejects those, once more without.</summary>
        static HttpWebResponse OpenSpeech(OpenAIClient c, string model, string voice, string text)
        {
            try
            {
                return c.Speech(model, voice, text, Personality.VoiceDirection, "pcm", 30000);
            }
            catch (OpenAIException ex)
            {
                string m = ((ex.ApiMessage ?? "") + " " + (ex.Param ?? "")).ToLowerInvariant();
                if (ex.Status != 400 || !m.Contains("instructions")) throw;
                DebugLog.Write("assistant: " + model + " won't take voice instructions; asking plainly");
                return c.Speech(model, voice, text, null, "pcm", 30000);
            }
        }

        /// <summary>Cut off whatever is being said (Finished is raised right away if something was).</summary>
        public void Stop()
        {
            Utterance u;
            lock (gate)
            {
                generation++;
                u = cur;
                cur = null;
                Monitor.PulseAll(gate);
            }
            if (u == null) return;
            if (u.Client != null) { try { u.Client.Abort(); } catch { } }
            var inflight = u.Req;
            if (inflight != null) { try { inflight.Abort(); } catch { } }
            if (u.Windows && u.WindowsBegun) postToUi(delegate { if (windowsOwner == u) { windowsOwner = null; windows.Stop(); } });
            Raise(Finished);
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            windows.Dispose();   // the module disposes us on the UI thread (posting would be dropped by then)
        }
    }

    /// <summary>Energy-based voice activity detection over 16-bit mono PCM chunks.</summary>
    internal sealed class VoiceActivity
    {
        public int SilenceStopMs = 700;       // stop after this much quiet once speech started (was 1.2 s; snappier)
        public int NoSpeechGiveUpMs = 8000;   // give up if nobody talks
        public int MaxMs = 30000;
        public int TotalMs, SpeechMs, SilenceMs, PeakAbs;
        public bool SpeechStarted;
        public string StopReason;             // null, "silence", "nothing", "max"
        public double LastRms, Threshold;
        double noise = -1;
        int speechRun;

        /// <summary>pushToTalk: silence never ends it and nobody-talking never gives up; only the 60 s cap does.</summary>
        public static VoiceActivity Create(bool pushToTalk)
        {
            var v = new VoiceActivity();
            if (pushToTalk)
            {
                v.SilenceStopMs = int.MaxValue;
                v.NoSpeechGiveUpMs = int.MaxValue;
                v.MaxMs = 60000;
            }
            return v;
        }

        public void Feed(byte[] buf, int count)
        {
            int samples = count / 2;
            if (samples <= 0) return;
            double sum = 0;
            for (int i = 0; i + 1 < count; i += 2)
            {
                short v = (short)(buf[i] | (buf[i + 1] << 8));
                int a = v < 0 ? -(int)v : v;
                if (a > PeakAbs) PeakAbs = a;
                sum += (double)v * v;
            }
            double rms = Math.Sqrt(sum / samples);
            int ms = samples * 1000 / VoiceRecorder.SampleRate;
            TotalMs += ms;
            LastRms = rms;
            if (noise < 0 || (TotalMs <= 300 && rms < noise)) noise = rms;
            // cap the floor: if they start talking the instant they press the key, the first chunk
            // is speech, not room noise, and must not raise the bar above normal speech levels.
            if (noise > 500) noise = 500;
            Threshold = Math.Max(180, noise * 2.5);   // some mics are quiet; don't mistake soft speech for silence
            if (rms > Threshold)
            {
                speechRun += ms;
                SilenceMs = 0;
                if (speechRun >= 200) SpeechStarted = true;
                if (SpeechStarted) SpeechMs += ms;
            }
            else
            {
                speechRun = 0;
                if (SpeechStarted) SilenceMs += ms;
                else noise = noise * 0.9 + rms * 0.1;
            }
            if (SpeechStarted && SilenceMs >= SilenceStopMs) StopReason = "silence";
            else if (!SpeechStarted && TotalMs >= NoSpeechGiveUpMs) StopReason = "nothing";
            else if (TotalMs >= MaxMs) StopReason = "max";
        }
    }

    internal static class VoiceWav
    {
        public static byte[] Build(byte[] pcm, int sampleRate, int bits, int channels)
        {
            var ms = new MemoryStream(44 + pcm.Length);
            var w = new BinaryWriter(ms);
            int blockAlign = channels * bits / 8;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + pcm.Length);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(sampleRate * blockAlign);
            w.Write((short)blockAlign);
            w.Write((short)bits);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);
            w.Flush();
            return ms.ToArray();
        }
    }

    /// <summary>Records the default mic until silence / 30 s / RequestStop, on its own thread.
    /// Finished(recorder, wav or null, reason, error or null) is raised on that thread.
    /// reason: "stopped", "silence", "max", "nothing", "short", "aborted".</summary>
    internal sealed class VoiceRecorder
    {
        public const int SampleRate = 16000;
        const int BufferMs = 100;
        const int BufferCount = 8;

        public event Action<VoiceRecorder, byte[], string, string> Finished;

        volatile bool stopRequested, abortRequested, recording;
        /// <summary>Set before Start: hold-to-talk (no silence auto-stop, 60 s cap; a too-short or
        /// speechless recording ends quietly with reason "quiet").</summary>
        public bool PushToTalk;
        /// <summary>The few seconds right after it answered: no wake word needed, and silence closes it quietly.</summary>
        public bool FollowUp;
        /// <summary>If &gt; 0: give up after this long without speech (instead of the usual 8 s).</summary>
        public int NoSpeechMs;
        /// <summary>How much actual speech it heard (set when it finishes).</summary>
        public int SpeechMs;
        /// <summary>A brain dump: minutes long, and pauses to think don't end it.</summary>
        public bool LongForm;
        public const int LongFormMaxMs = 8 * 60 * 1000;
        IntPtr handle;
        IntPtr[] headers, buffers;
        int hdrSize, flagsOffset, bytesOffset;

        public bool IsRecording { get { return recording; } }
        public void RequestStop() { stopRequested = true; }
        public void Abort() { abortRequested = true; }

        static int BufferBytes { get { return SampleRate * 2 * BufferMs / 1000; } }

        public bool Start(out string error)
        {
            error = null;
            int devs;
            try { devs = VoiceNative.waveInGetNumDevs(); }
            catch (Exception ex) { error = "i can't reach the sound system (" + ex.Message + ")."; return false; }
            if (devs <= 0) { error = "i can't find a microphone. plug one in and try again?"; return false; }

            var fmt = new VoiceNative.WAVEFORMATEX();
            fmt.wFormatTag = 1;
            fmt.nChannels = 1;
            fmt.nSamplesPerSec = SampleRate;
            fmt.wBitsPerSample = 16;
            fmt.nBlockAlign = 2;
            fmt.nAvgBytesPerSec = SampleRate * 2;
            fmt.cbSize = 0;
            int rc = VoiceNative.waveInOpen(out handle, VoiceNative.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, VoiceNative.CALLBACK_NULL);
            if (rc != 0)
            {
                handle = IntPtr.Zero;
                error = rc == 4 ? "the mic is busy with something else right now."
                    : "i couldn't open the microphone (error " + rc + "). windows might be keeping desktop apps away from it (settings → privacy & security → microphone).";
                return false;
            }
            hdrSize = Marshal.SizeOf(typeof(VoiceNative.WAVEHDR));
            flagsOffset = Marshal.OffsetOf(typeof(VoiceNative.WAVEHDR), "dwFlags").ToInt32();
            bytesOffset = Marshal.OffsetOf(typeof(VoiceNative.WAVEHDR), "dwBytesRecorded").ToInt32();
            headers = new IntPtr[BufferCount];
            buffers = new IntPtr[BufferCount];
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    buffers[i] = Marshal.AllocHGlobal(BufferBytes);
                    headers[i] = Marshal.AllocHGlobal(hdrSize);
                    var h = new VoiceNative.WAVEHDR();
                    h.lpData = buffers[i];
                    h.dwBufferLength = (uint)BufferBytes;
                    Marshal.StructureToPtr(h, headers[i], false);
                    Check(VoiceNative.waveInPrepareHeader(handle, headers[i], (uint)hdrSize), "prepare");
                    Check(VoiceNative.waveInAddBuffer(handle, headers[i], (uint)hdrSize), "add buffer");
                }
                Check(VoiceNative.waveInStart(handle), "start");
            }
            catch (Exception ex)
            {
                Cleanup();
                error = "the microphone wouldn't start (" + ex.Message + ").";
                return false;
            }
            recording = true;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "bestie-mic";
            t.Start();
            return true;
        }

        static void Check(int rc, string what)
        {
            if (rc != 0) throw new InvalidOperationException(what + " failed, error " + rc);
        }

        bool IsDone(IntPtr hdr) { return (Marshal.ReadInt32(hdr, flagsOffset) & VoiceNative.WHDR_DONE) != 0; }

        void Loop()
        {
            var pcm = new MemoryStream();
            var vad = VoiceActivity.Create(PushToTalk);
            if (NoSpeechMs > 0) vad.NoSpeechGiveUpMs = NoSpeechMs;
            if (LongForm) { vad.SilenceStopMs = 7000; vad.NoSpeechGiveUpMs = 20000; vad.MaxMs = LongFormMaxMs; }
            var chunk = new byte[BufferBytes];
            string reason = "stopped", error = null;
            int next = 0;
            try
            {
                while (true)
                {
                    if (abortRequested) { reason = "aborted"; break; }
                    bool got = false;
                    while (IsDone(headers[next]))
                    {
                        int n = Marshal.ReadInt32(headers[next], bytesOffset);
                        if (n > 0)
                        {
                            Marshal.Copy(buffers[next], chunk, 0, n);
                            pcm.Write(chunk, 0, n);
                            vad.Feed(chunk, n);
                        }
                        Marshal.WriteInt32(headers[next], bytesOffset, 0);
                        Marshal.WriteInt32(headers[next], flagsOffset, Marshal.ReadInt32(headers[next], flagsOffset) & ~VoiceNative.WHDR_DONE);
                        VoiceNative.waveInAddBuffer(handle, headers[next], (uint)hdrSize);
                        next = (next + 1) % BufferCount;
                        got = true;
                        if (vad.StopReason != null) break;
                    }
                    if (stopRequested) { reason = "stopped"; break; }
                    if (vad.StopReason != null) { reason = vad.StopReason; break; }
                    Thread.Sleep(got ? 5 : 20);
                }
                VoiceNative.waveInReset(handle);   // hands back the buffers in flight
                if (reason != "aborted")
                {
                    for (int i = 0; i < BufferCount; i++)
                    {
                        int idx = (next + i) % BufferCount;
                        if (!IsDone(headers[idx])) continue;
                        int n = Marshal.ReadInt32(headers[idx], bytesOffset);
                        if (n <= 0) continue;
                        Marshal.Copy(buffers[idx], chunk, 0, n);
                        pcm.Write(chunk, 0, n);
                        vad.Feed(chunk, n);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("assistant: mic loop: " + ex.Message);
                error = "the microphone glitched. try again?";
            }
            finally
            {
                Cleanup();
                recording = false;
            }

            byte[] wav = null;
            if (reason != "aborted" && error == null)
            {
                if (vad.PeakAbs == 0 && vad.TotalMs >= 500)
                    error = "i only heard perfect silence. windows might be keeping desktop apps away from the mic (settings → privacy & security → microphone).";
                else if (PushToTalk && reason == "stopped" && (vad.TotalMs < 600 || vad.PeakAbs < 250))
                    reason = "quiet";   // a click, or a hold in total silence: drop it without a fuss
                else if (vad.SpeechStarted || (reason == "stopped" && vad.TotalMs >= 600 && vad.PeakAbs > 250))
                    wav = VoiceWav.Build(Louder(pcm.ToArray(), vad.PeakAbs), SampleRate, 16, 1);   // a deliberate hold always gets heard; the transcriber decides
                else if (reason == "stopped") reason = "short";
            }
            SpeechMs = vad.SpeechMs;
            DebugLog.Write("assistant: mic done (" + reason + ", " + vad.TotalMs + " ms, speech " + vad.SpeechMs + " ms, peak " + vad.PeakAbs + ")");
            var f = Finished;
            if (f != null)
            {
                try { f(this, wav, reason, error); }
                catch (Exception ex) { DebugLog.Write("assistant: mic finished handler: " + ex); }
            }
        }

        /// <summary>Quiet mics make for bad transcripts: bring the loudest peak up to ~60% (at most 8x louder).</summary>
        static byte[] Louder(byte[] pcm, int peak)
        {
            if (peak <= 0 || peak >= 20000) return pcm;
            double gain = Math.Min(8.0, 20000.0 / peak);
            for (int i = 0; i + 1 < pcm.Length; i += 2)
            {
                int s = (int)Math.Round((short)(pcm[i] | (pcm[i + 1] << 8)) * gain);
                s = Math.Max(-32768, Math.Min(32767, s));
                pcm[i] = (byte)(s & 0xFF);
                pcm[i + 1] = (byte)((s >> 8) & 0xFF);
            }
            return pcm;
        }

        void Cleanup()
        {
            if (handle != IntPtr.Zero)
            {
                try { VoiceNative.waveInReset(handle); } catch { }
                if (headers != null)
                    foreach (IntPtr h in headers)
                        if (h != IntPtr.Zero) { try { VoiceNative.waveInUnprepareHeader(handle, h, (uint)hdrSize); } catch { } }
                try { VoiceNative.waveInClose(handle); } catch { }
                handle = IntPtr.Zero;
            }
            if (headers != null) for (int i = 0; i < headers.Length; i++) if (headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(headers[i]); headers[i] = IntPtr.Zero; }
            if (buffers != null) for (int i = 0; i < buffers.Length; i++) if (buffers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(buffers[i]); buffers[i] = IntPtr.Zero; }
        }
    }

    /// <summary>Local, free text-to-speech. Create/use on the UI thread so events come back there.
    /// Begin / Enqueue (any number of sentences, SAPI queues them) / End; Finished once the queue drains after End.</summary>
    internal sealed class VoiceTts : IDisposable
    {
        SpeechSynthesizer synth;
        readonly List<Prompt> prompts = new List<Prompt>();
        bool active, ended, startedRaised;
        bool disposed;

        public event Action Started;
        public event Action Finished;

        public bool Speaking { get { return active; } }

        public void Begin(string voice, int rate)
        {
            if (disposed) return;
            Stop();
            try
            {
                if (synth == null)
                {
                    synth = new SpeechSynthesizer();
                    synth.SetOutputToDefaultAudioDevice();
                    synth.SpeakCompleted += OnCompleted;
                }
                if (!string.IsNullOrEmpty(voice))
                {
                    try { synth.SelectVoice(voice); }
                    catch (Exception ex) { DebugLog.Write("assistant: voice '" + voice + "' unavailable: " + ex.Message); }
                }
                synth.Rate = Math.Max(-10, Math.Min(10, rate));
                synth.Volume = 100;
                active = true;
                ended = false;
                startedRaised = false;
            }
            catch (Exception ex) { DebugLog.Write("assistant: tts: " + ex.Message); active = false; }
        }

        public void Enqueue(string text)
        {
            if (!active || synth == null || string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            try
            {
                prompts.Add(synth.SpeakAsync(text));
                if (!startedRaised)
                {
                    startedRaised = true;
                    var s = Started;
                    if (s != null) s();
                }
            }
            catch (Exception ex) { DebugLog.Write("assistant: tts: " + ex.Message); }
        }

        public void End()
        {
            if (!active) return;
            ended = true;
            if (prompts.Count == 0) Done();
        }

        void Done()
        {
            active = false;
            var f = Finished;
            if (f != null) { try { f(); } catch (Exception ex) { DebugLog.Write("assistant: tts finished: " + ex.Message); } }
        }

        public static string[] InstalledVoices()
        {
            try
            {
                using (var s = new SpeechSynthesizer())
                    return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToArray();
            }
            catch (Exception ex)
            {
                DebugLog.Write("assistant: voices: " + ex.Message);
                return new string[0];
            }
        }

        public void Speak(string text, string voice, int rate)
        {
            Begin(voice, rate);
            Enqueue(text);
            End();
        }

        void OnCompleted(object sender, SpeakCompletedEventArgs e)
        {
            if (!prompts.Remove(e.Prompt)) return;   // an older, cancelled prompt
            if (active && ended && prompts.Count == 0) Done();
        }

        public void Stop()
        {
            if (!active) return;
            prompts.Clear();
            try { if (synth != null) synth.SpeakAsyncCancelAll(); } catch { }
            Done();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            active = false;
            prompts.Clear();
            if (synth != null)
            {
                try { synth.SpeakAsyncCancelAll(); } catch { }
                try { synth.Dispose(); } catch { }
                synth = null;
            }
        }
    }
}
