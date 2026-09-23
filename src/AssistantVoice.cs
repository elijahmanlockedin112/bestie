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
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);
    }

    /// <summary>Plays 16-bit mono PCM from a stream through waveOut while it downloads.</summary>
    internal static class PcmStreamPlayer
    {
        /// <summary>Blocks until the audio has played, the stream ends, or cancelled() turns true.
        /// firstAudio runs once, right when sound starts. Returns bytes queued for playback.</summary>
        public static long Play(Stream src, int sampleRate, Func<bool> cancelled, Action firstAudio)
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
            int hdrSize = Marshal.SizeOf(typeof(VoiceNative.WAVEHDR));
            int flagsOffset = Marshal.OffsetOf(typeof(VoiceNative.WAVEHDR), "dwFlags").ToInt32();
            var pending = new List<IntPtr[]>();     // { header, data }
            int chunk = sampleRate * 2 / 4;         // 250 ms per buffer
            int prebuffer = sampleRate * 2 * 2 / 5; // wait for 400 ms before starting (avoids stutter)
            var acc = new MemoryStream();
            var buf = new byte[16384];
            long written = 0;
            bool started = false;
            Action<byte[], int> write = delegate(byte[] data, int count)
            {
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
                    int n;
                    try { n = src.Read(buf, 0, buf.Length); }
                    catch { if (cancelled()) break; throw; }
                    if (n <= 0) break;
                    acc.Write(buf, 0, n);
                    if (acc.Length >= (started ? chunk : prebuffer)) flush();
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

    /// <summary>Speaks replies: OpenAI text-to-speech (streamed, steered with Personality.VoiceDirection)
    /// when chosen and a key exists, else the free windows voice. Any OpenAI failure before sound starts
    /// falls back to the windows voice (logged). A new Speak, Stop or Dispose cuts off the current one.
    /// Call Speak/Stop on the UI thread. Started/Finished may be raised on any thread.</summary>
    internal sealed class AssistantSpeaker : IDisposable
    {
        public const int PcmRate = 24000;   // /audio/speech "pcm" = 24 kHz 16-bit mono little-endian

        /// <summary>Tests: replaces the windows voice (gets the text instead of speaking it).</summary>
#pragma warning disable 649   // only tests set it
        internal static Action<string> TestWindowsVoiceHook;
#pragma warning restore 649

        readonly VoiceTts windows = new VoiceTts();
        readonly Action<Action> postToUi;
        int generation;
        volatile bool openAiActive;
        volatile OpenAIClient client;
        volatile bool disposed;

        public event Action Started;
        public event Action Finished;

        public AssistantSpeaker(Action<Action> postToUi)
        {
            this.postToUi = postToUi;
            windows.Started += delegate { Raise(Started); };
            windows.Finished += delegate { Raise(Finished); };
        }

        public bool Speaking { get { return openAiActive || windows.Speaking; } }

        void Raise(Action a)
        {
            if (a == null) return;
            try { a(); } catch (Exception ex) { DebugLog.Write("assistant: speaker event: " + ex.Message); }
        }

        public void Speak(string text, AssistantSettings s)
        {
            if (disposed) return;
            Stop();
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
            if (s.UseOpenAiVoice) SpeakOpenAi(text, s);
            else SpeakWindows(text, s);
        }

        void SpeakWindows(string text, AssistantSettings s)
        {
            Action<string> hook = TestWindowsVoiceHook;
            if (hook != null) { Raise(Started); hook(text); Raise(Finished); return; }
            windows.Speak(text, s.TtsVoice, s.TtsRate);
        }

        void SpeakOpenAi(string text, AssistantSettings s)
        {
            int gen = Interlocked.Increment(ref generation);
            var c = new OpenAIClient(s.BaseUrl, s.ApiKey);
            client = c;
            openAiActive = true;
            string model = string.IsNullOrEmpty(s.TtsModel) ? AssistantSettings.DefaultTtsModel : s.TtsModel;
            string voice = string.IsNullOrEmpty(s.OpenAiVoice) ? AssistantSettings.DefaultOpenAiVoice : s.OpenAiVoice;
            AssistantSettings snap = s.Clone();
            var t = new Thread(delegate()
            {
                bool played = false;
                string fail = null;
                Func<bool> stale = delegate { return disposed || gen != Volatile(ref generation); };
                try
                {
                    using (HttpWebResponse resp = OpenSpeech(c, model, voice, text))
                    using (Stream audio = resp.GetResponseStream())
                    {
                        PcmStreamPlayer.Play(audio, PcmRate, stale, delegate
                        {
                            played = true;
                            if (!stale()) Raise(Started);
                        });
                    }
                }
                catch (OpenAIException ex) { if (!ex.Aborted) fail = ex.Message; }
                catch (Exception ex) { if (!stale()) fail = ex.GetType().Name + ": " + ex.Message; }
                if (stale()) return;              // superseded or stopped: Stop() already said Finished
                openAiActive = false;
                client = null;
                if (fail != null && !played)
                {
                    DebugLog.Write("assistant: openai voice failed (" + fail + "); using the windows voice");
                    postToUi(delegate { if (gen == Volatile(ref generation) && !disposed) SpeakWindows(text, snap); else Raise(Finished); });
                    return;
                }
                if (fail != null) DebugLog.Write("assistant: openai voice broke mid-sentence: " + fail);
                Raise(Finished);
            });
            t.IsBackground = true;
            t.Name = "bestie-voice";
            t.Start();
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

        static int Volatile(ref int v) { return Interlocked.CompareExchange(ref v, 0, 0); }

        public void Stop()
        {
            Interlocked.Increment(ref generation);
            OpenAIClient c = client;
            client = null;
            if (c != null) { try { c.Abort(); } catch { } }
            bool wasActive = openAiActive;
            openAiActive = false;
            windows.Stop();
            if (wasActive) Raise(Finished);
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            windows.Dispose();
        }
    }

    /// <summary>Energy-based voice activity detection over 16-bit mono PCM chunks.</summary>
    internal sealed class VoiceActivity
    {
        public int SilenceStopMs = 1200;      // stop after this much quiet once speech started
        public int NoSpeechGiveUpMs = 8000;   // give up if nobody talks
        public int MaxMs = 30000;
        public int TotalMs, SpeechMs, SilenceMs, PeakAbs;
        public bool SpeechStarted;
        public string StopReason;             // null, "silence", "nothing", "max"
        public double LastRms, Threshold;
        double noise = -1;
        int speechRun;

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
            Threshold = Math.Max(350, noise * 3.0);
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
            var vad = new VoiceActivity();
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
                else if (vad.SpeechStarted || (reason == "stopped" && vad.TotalMs >= 700 && vad.PeakAbs > 400))
                    wav = VoiceWav.Build(pcm.ToArray(), SampleRate, 16, 1);
                else if (reason == "stopped") reason = "short";
            }
            DebugLog.Write("assistant: mic done (" + reason + ", " + vad.TotalMs + " ms, speech " + vad.SpeechMs + " ms, peak " + vad.PeakAbs + ")");
            var f = Finished;
            if (f != null)
            {
                try { f(this, wav, reason, error); }
                catch (Exception ex) { DebugLog.Write("assistant: mic finished handler: " + ex); }
            }
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

    /// <summary>Local, free text-to-speech. Create/use on the UI thread so events come back there.</summary>
    internal sealed class VoiceTts : IDisposable
    {
        SpeechSynthesizer synth;
        Prompt current;
        bool disposed;

        public event Action Started;
        public event Action Finished;

        public bool Speaking { get { return current != null; } }

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
            if (disposed) return;
            Stop();
            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
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
                current = synth.SpeakAsync(text);
                var s = Started;
                if (s != null) s();
            }
            catch (Exception ex)
            {
                DebugLog.Write("assistant: tts: " + ex.Message);
                current = null;
                var f = Finished;
                if (f != null) f();
            }
        }

        void OnCompleted(object sender, SpeakCompletedEventArgs e)
        {
            if (e.Prompt != current) return;   // an older, cancelled prompt
            current = null;
            var f = Finished;
            if (f != null) { try { f(); } catch (Exception ex) { DebugLog.Write("assistant: tts finished: " + ex.Message); } }
        }

        public void Stop()
        {
            if (synth == null || current == null) return;
            current = null;
            try { synth.SpeakAsyncCancelAll(); } catch { }
            var f = Finished;
            if (f != null) { try { f(); } catch { } }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            current = null;
            if (synth != null)
            {
                try { synth.SpeakAsyncCancelAll(); } catch { }
                try { synth.Dispose(); } catch { }
                synth = null;
            }
        }
    }
}
