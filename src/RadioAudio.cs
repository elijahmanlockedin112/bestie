// RadioAudio.cs - the sound of Locked In FM, made from scratch: the station jingle (a whoosh, a bell arpeggio and a bass
// hit, with the station ID spoken over the tail), the phone-line sound for callers (band-limited to 300-3400 Hz, a little
// grit and line hiss), and the plumbing to stitch pieces together. All 24 kHz, 16-bit mono PCM: what the voice pipeline
// already plays. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bestie
{
    internal static class RadioAudio
    {
        public const int Rate = 24000;

        // ------------------------------------------------------------------ pcm <-> floats

        public static float[] ToFloats(byte[] pcm)
        {
            var f = new float[pcm.Length / 2];
            for (int i = 0; i < f.Length; i++) f[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
            return f;
        }

        public static byte[] ToPcm(float[] f)
        {
            var b = new byte[f.Length * 2];
            for (int i = 0; i < f.Length; i++)
            {
                float v = Math.Max(-1f, Math.Min(1f, f[i]));
                short s = (short)Math.Round(v * 32767);
                b[2 * i] = (byte)(s & 0xFF); b[2 * i + 1] = (byte)((s >> 8) & 0xFF);
            }
            return b;
        }

        public static byte[] Silence(double seconds) { return new byte[(int)(seconds * Rate) * 2]; }

        /// <summary>Evens out loudness: scales so the loud parts sit at about rmsTarget (voices come back anywhere from
        /// whisper-quiet to hot), never letting a peak past peakMax. The player's voice gain goes on top.</summary>
        public static byte[] Level(byte[] pcm, double rmsTarget, double peakMax)
        {
            if (pcm == null || pcm.Length < 200) return pcm;
            float[] f = ToFloats(pcm);
            // loudness of the voiced part only (pauses would drag a plain average down)
            var loud = f.Where(x => Math.Abs(x) > 0.01f).ToArray();
            if (loud.Length < 100) return pcm;
            double rms = Math.Sqrt(loud.Average(x => (double)x * x)), peak = loud.Max(x => Math.Abs(x));
            double k = Math.Min(rmsTarget / Math.Max(1e-6, rms), peakMax / Math.Max(1e-6, peak));
            for (int i = 0; i < f.Length; i++) f[i] = (float)(f[i] * k);
            return ToPcm(f);
        }

        public static byte[] Join(IEnumerable<byte[]> parts)
        {
            var ms = new MemoryStream();
            foreach (byte[] p in parts) if (p != null) ms.Write(p, 0, p.Length - (p.Length % 2));
            return ms.ToArray();
        }

        /// <summary>Adds b into a starting at offsetSeconds (a grows if needed), with a soft limiter.</summary>
        public static float[] MixInto(float[] a, float[] b, double offsetSeconds, float gainB)
        {
            int off = (int)(offsetSeconds * Rate);
            var o = new float[Math.Max(a.Length, off + b.Length)];
            Array.Copy(a, o, a.Length);
            for (int i = 0; i < b.Length; i++) o[off + i] += b[i] * gainB;
            for (int i = 0; i < o.Length; i++) o[i] = (float)Math.Tanh(o[i] * 1.1) / (float)Math.Tanh(1.1);
            return o;
        }

        // ------------------------------------------------------------------ the jingle

        /// <summary>The music bed of the station ID (about 2.1 s): a rising whoosh, four bright bell notes, a bass hit, a
        /// shimmering chord. The spoken "locked in f m" goes on top at ~0.95 s (see Jingle).</summary>
        public static float[] JingleBed()
        {
            int n = (int)(2.1 * Rate);
            var o = new float[n];
            var rng = new Random(7);
            // whoosh: noise through a sweeping one-pole lowpass, swelling up to the hit
            double lp = 0;
            for (int i = 0; i < (int)(0.55 * Rate); i++)
            {
                double t = i / (double)Rate, k = t / 0.55;
                double cutoff = 0.02 + 0.35 * k * k;
                lp += cutoff * ((rng.NextDouble() * 2 - 1) - lp);
                o[i] += (float)(lp * 0.55 * k * k);
            }
            // bells: C6 E6 G6 C7, a sixteenth apart, each a sine plus a bright partial, fast attack, ringing decay
            double[] notes = { 1046.5, 1318.5, 1568.0, 2093.0 };
            for (int nIdx = 0; nIdx < notes.Length; nIdx++)
            {
                int start = (int)((0.42 + nIdx * 0.085) * Rate);
                for (int i = 0; start + i < n; i++)
                {
                    double t = i / (double)Rate;
                    double env = Math.Min(1, t / 0.004) * Math.Exp(-t * 4.2);
                    double f = notes[nIdx];
                    o[start + i] += (float)(env * 0.16 * (Math.Sin(2 * Math.PI * f * t) + 0.35 * Math.Sin(2 * Math.PI * f * 2.76 * t) * Math.Exp(-t * 9)));
                }
            }
            // bass hit + a held major-seventh chord underneath, fading out
            int hit = (int)(0.42 * Rate);
            double[] chord = { 130.8, 164.8, 196.0, 246.9, 329.6 };
            for (int i = 0; hit + i < n; i++)
            {
                double t = i / (double)Rate;
                double bass = Math.Sin(2 * Math.PI * (55 + 40 * Math.Exp(-t * 30)) * t) * Math.Exp(-t * 5) * 0.5;
                double pad = 0;
                foreach (double f in chord) pad += Math.Sin(2 * Math.PI * f * t + Math.Sin(2 * Math.PI * 5 * t) * 0.15);
                double padEnv = Math.Min(1, t / 0.08) * Math.Max(0, 1 - t / 1.68) * 0.055;
                o[hit + i] += (float)(bass + pad * padEnv);
            }
            // a last little sparkle on the way out
            int sp = (int)(1.55 * Rate);
            for (int i = 0; sp + i < n; i++)
            {
                double t = i / (double)Rate;
                o[sp + i] += (float)(Math.Sin(2 * Math.PI * 2637 * t) * Math.Exp(-t * 7) * 0.07);
            }
            return o;
        }

        /// <summary>The finished station ID: the bed with the spoken tag (pcm from the dj voice) laid over its tail.</summary>
        public static byte[] Jingle(byte[] spokenTag)
        {
            float[] bed = JingleBed();
            if (spokenTag == null || spokenTag.Length < 100) return ToPcm(bed);
            return ToPcm(MixInto(bed, ToFloats(spokenTag), 0.95, 1.0f));
        }

        // ------------------------------------------------------------------ callers: the phone line

        /// <summary>Makes a voice sound like it's coming down a phone line: 300-3400 Hz, a touch of grit, faint hiss.</summary>
        public static byte[] PhoneLine(byte[] pcm)
        {
            float[] x = ToFloats(pcm);
            Biquad hp = Biquad.HighPass(320, 0.7), hp2 = Biquad.HighPass(320, 0.7), lp = Biquad.LowPass(3300, 0.7), lp2 = Biquad.LowPass(3300, 0.7);
            var rng = new Random(11);
            for (int i = 0; i < x.Length; i++)
            {
                double v = lp2.Run(lp.Run(hp2.Run(hp.Run(x[i]))));
                v = Math.Tanh(v * 2.2) * 0.62;                       // a cheap line, slightly overdriven
                v += (rng.NextDouble() * 2 - 1) * 0.004;             // hiss
                x[i] = (float)v;
            }
            return ToPcm(x);
        }

        /// <summary>The two-tone "you're on the air" beep before a caller.</summary>
        public static byte[] CallerBeep()
        {
            int n = (int)(0.32 * Rate);
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double f = t < 0.16 ? 1400 : 1050;
                double env = Math.Min(1, (t % 0.16) / 0.005) * Math.Min(1, (0.16 - (t % 0.16)) / 0.01);
                o[i] = (float)(Math.Sin(2 * Math.PI * f * t) * env * 0.18);
            }
            return ToPcm(o);
        }

        sealed class Biquad
        {
            double b0, b1, b2, a1, a2, x1, x2, y1, y2;
            public double Run(double x)
            {
                double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                return y;
            }
            static Biquad Make(double f, double q, bool high)
            {
                double w = 2 * Math.PI * f / Rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
                var bq = new Biquad();
                if (high) { bq.b0 = (1 + cos) / 2 / a0; bq.b1 = -(1 + cos) / a0; bq.b2 = (1 + cos) / 2 / a0; }
                else { bq.b0 = (1 - cos) / 2 / a0; bq.b1 = (1 - cos) / a0; bq.b2 = (1 - cos) / 2 / a0; }
                bq.a1 = -2 * cos / a0; bq.a2 = (1 - alpha) / a0;
                return bq;
            }
            public static Biquad HighPass(double f, double q) { return Make(f, q, true); }
            public static Biquad LowPass(double f, double q) { return Make(f, q, false); }
        }
    }
}
