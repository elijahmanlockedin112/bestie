// LockInSound.cs - the Locked In sound, made from scratch (no audio files to ship): an airlock hiss, a power-up sweep
// that climbs into a heavy "lock" hit with a low thump and a wide chord, and a little rising sparkle on top. About
// 2.6 seconds, 44.1 kHz mono WAV, for the phone's Locked In screen and the iPhone Shortcut. C# 5 only.
using System;
using System.IO;
using System.Text;

namespace Bestie
{
    internal static class LockInSound
    {
        const int Rate = 44100;
        const double Seconds = 2.6, Hit = 0.82;   // when the lock lands
        static byte[] cached;

        public static byte[] Wav()
        {
            if (cached == null) cached = Encode(Render());
            return cached;
        }

        static double[] Render()
        {
            int n = (int)(Rate * Seconds);
            var x = new double[n];
            var rng = new Random(7);
            double sweepPhase = 0, lp = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / Rate, v = 0;

                // airlock hiss: filtered noise that opens and closes in the first half second
                double noise = rng.NextDouble() * 2 - 1;
                lp += (noise - lp) * (0.08 + 0.25 * Math.Min(1, t / 0.3));
                if (t < 0.6) v += lp * 0.35 * Math.Sin(Math.PI * Math.Min(1, t / 0.6));

                // power-up sweep: 70 Hz to 1.1 kHz, accelerating, with a little wobble, gone at the hit
                if (t < Hit)
                {
                    double p = t / Hit, f = 70 * Math.Pow(1100 / 70.0, p * p);
                    sweepPhase += 2 * Math.PI * f / Rate;
                    double wob = 1 + 0.004 * Math.Sin(2 * Math.PI * 9 * t);
                    v += (Math.Sin(sweepPhase * wob) + 0.3 * Math.Sin(2 * sweepPhase)) * 0.28 * p * p;
                }

                double d = t - Hit;
                if (d >= 0)
                {
                    // the lock: a low thump that drops in pitch
                    double thumpF = 48 + 60 * Math.Exp(-d * 30);
                    v += Math.Sin(2 * Math.PI * thumpF * d) * 0.95 * Math.Exp(-d * 6.5);
                    // a short metallic click on top of it
                    if (d < 0.03) v += noise * 0.5 * (1 - d / 0.03);
                    // a wide, slightly detuned chord (A, E, A, C#) that rings out
                    double[] notes = { 220, 329.63, 440, 554.37 };
                    double chord = 0;
                    foreach (double nf in notes)
                        chord += Saw(nf * d) * 0.5 + Saw(nf * 1.006 * d) * 0.5;
                    v += chord * 0.07 * Math.Exp(-d * 1.6) * Math.Min(1, d / 0.01);
                    // sparkle: four quick rising blips just after the hit
                    double[] blips = { 1318.5, 1760, 2217.5, 2637 };
                    for (int b = 0; b < blips.Length; b++)
                    {
                        double bd = d - 0.14 - b * 0.075;
                        if (bd >= 0 && bd < 0.35) v += Math.Sin(2 * Math.PI * blips[b] * bd) * 0.16 * Math.Exp(-bd * 14);
                    }
                }
                // fade the very end so it never clicks
                if (t > Seconds - 0.15) v *= (Seconds - t) / 0.15;
                x[i] = v;
            }
            // soft limit, then bring the loudest moment to about -1 dB
            double peak = 0;
            for (int i = 0; i < n; i++) { x[i] = Math.Tanh(x[i] * 1.3); peak = Math.Max(peak, Math.Abs(x[i])); }
            double gain = peak > 0 ? 0.89 / peak : 1;
            for (int i = 0; i < n; i++) x[i] *= gain;
            return x;
        }

        static double Saw(double cycles) { double f = cycles - Math.Floor(cycles); return 2 * f - 1; }

        static byte[] Encode(double[] x)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                int data = x.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
                foreach (double v in x) w.Write((short)Math.Round(Math.Max(-1, Math.Min(1, v)) * 32767));
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
