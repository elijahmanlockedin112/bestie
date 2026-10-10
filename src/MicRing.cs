// MicRing.cs - the last few seconds from the mic, kept the whole time the wake word listens. The windows recognizer only
// hands over audio once the whole sentence is over (and a second of silence after it), so the moment it *thinks* it heard
// "hey bestie" the words are taken from here and checked right away: it lights up while they're still talking.
// Nothing here leaves the computer by itself. C# 5 only.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Bestie
{
    internal sealed class MicRing : IDisposable
    {
        public const int Rate = 16000;
        const int BufferMs = 100, BufferCount = 6, KeepSeconds = 8;
        static int BufferBytes { get { return Rate * 2 * BufferMs / 1000; } }

        readonly byte[] ring = new byte[Rate * 2 * KeepSeconds];
        readonly object gate = new object();
        long written;   // bytes ever captured: positions in the stream
        IntPtr handle;
        IntPtr[] headers, buffers;
        int hdrSize, flagsOffset, bytesOffset;
        volatile bool stop;

        /// <summary>Where the stream is now (pass to Since later).</summary>
        public long Position { get { lock (gate) return written; } }

        public static long Bytes(double seconds) { return (long)(seconds * Rate) * 2; }

        public bool Start()
        {
            var fmt = new VoiceNative.WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = Rate * 2 };
            try
            {
                if (VoiceNative.waveInGetNumDevs() <= 0) return false;
                if (VoiceNative.waveInOpen(out handle, VoiceNative.WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, VoiceNative.CALLBACK_NULL) != 0) { handle = IntPtr.Zero; return false; }
                hdrSize = Marshal.SizeOf(typeof(VoiceNative.WAVEHDR));
                flagsOffset = Marshal.OffsetOf(typeof(VoiceNative.WAVEHDR), "dwFlags").ToInt32();
                bytesOffset = Marshal.OffsetOf(typeof(VoiceNative.WAVEHDR), "dwBytesRecorded").ToInt32();
                headers = new IntPtr[BufferCount];
                buffers = new IntPtr[BufferCount];
                for (int i = 0; i < BufferCount; i++)
                {
                    buffers[i] = Marshal.AllocHGlobal(BufferBytes);
                    headers[i] = Marshal.AllocHGlobal(hdrSize);
                    Marshal.StructureToPtr(new VoiceNative.WAVEHDR { lpData = buffers[i], dwBufferLength = (uint)BufferBytes }, headers[i], false);
                    if (VoiceNative.waveInPrepareHeader(handle, headers[i], (uint)hdrSize) != 0 || VoiceNative.waveInAddBuffer(handle, headers[i], (uint)hdrSize) != 0)
                        throw new InvalidOperationException("buffer");
                }
                if (VoiceNative.waveInStart(handle) != 0) throw new InvalidOperationException("start");
            }
            catch (Exception ex) { DebugLog.Write("wake word: no early ears (" + ex.Message + ")"); Cleanup(); return false; }
            new Thread(Loop) { IsBackground = true, Name = "bestie-mic-ring" }.Start();
            return true;
        }

        bool IsDone(IntPtr hdr) { return (Marshal.ReadInt32(hdr, flagsOffset) & VoiceNative.WHDR_DONE) != 0; }

        void Loop()
        {
            var chunk = new byte[BufferBytes];
            int next = 0;
            try
            {
                while (!stop)
                {
                    bool got = false;
                    while (!stop && IsDone(headers[next]))
                    {
                        int n = Marshal.ReadInt32(headers[next], bytesOffset);
                        if (n > 0)
                        {
                            Marshal.Copy(buffers[next], chunk, 0, n);
                            lock (gate)
                            {
                                for (int i = 0; i < n; i++) ring[(int)((written + i) % ring.Length)] = chunk[i];
                                written += n;
                            }
                        }
                        Marshal.WriteInt32(headers[next], bytesOffset, 0);
                        Marshal.WriteInt32(headers[next], flagsOffset, Marshal.ReadInt32(headers[next], flagsOffset) & ~VoiceNative.WHDR_DONE);
                        VoiceNative.waveInAddBuffer(handle, headers[next], (uint)hdrSize);
                        next = (next + 1) % BufferCount;
                        got = true;
                    }
                    Thread.Sleep(got ? 10 : 30);
                }
            }
            catch (Exception ex) { DebugLog.Write("wake word: early ears: " + ex.Message); }
            finally { Cleanup(); }
        }

        /// <summary>A WAV of what came in since this position (at most what's still kept), made louder for a quiet mic.</summary>
        public byte[] WavSince(long from)
        {
            byte[] pcm;
            lock (gate)
            {
                from = Math.Max(from, Math.Max(0, written - ring.Length));
                from -= from % 2;
                pcm = new byte[(int)(written - from)];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = ring[(int)((from + i) % ring.Length)];
            }
            int peak = 0;
            for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8))));
            return VoiceWav.Build(VoiceRecorder.Louder(pcm, peak), Rate, 16, 1);
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

        public void Dispose() { stop = true; }
    }
}
