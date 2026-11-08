using System;
using System.IO;
using System.Media;

namespace GraphiteCompanion.Services
{
    /// <summary>Short confirmation tones generated in memory (no sound files needed).</summary>
    public static class Beeper
    {
        private static readonly byte[] Down = Tone(new[] { 880.0, 587.0 });
        private static readonly byte[] Up = Tone(new[] { 587.0, 880.0 });

        public static void MicMuted() => Play(Down);
        public static void MicUnmuted() => Play(Up);

        private static void Play(byte[] wav)
        {
            try
            {
                var player = new SoundPlayer(new MemoryStream(wav));
                player.Play();
            }
            catch { }
        }

        private static byte[] Tone(double[] freqs)
        {
            const int rate = 44100;
            const double noteSec = 0.075;
            int perNote = (int)(rate * noteSec);
            int samples = perNote * freqs.Length;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8.ToArray()); w.Write(36 + samples * 2); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(samples * 2);
            for (int n = 0; n < freqs.Length; n++)
            {
                for (int i = 0; i < perNote; i++)
                {
                    double t = (double)i / rate;
                    double env = Math.Min(1, Math.Min(i, perNote - i) / (rate * 0.008));
                    w.Write((short)(Math.Sin(2 * Math.PI * freqs[n] * t) * env * 0.25 * short.MaxValue));
                }
            }
            return ms.ToArray();
        }
    }
}
