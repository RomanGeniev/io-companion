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
        private static readonly byte[] Chime = TimerChime();

        public static void MicMuted() => Play(Down);
        public static void MicUnmuted() => Play(Up);
        public static void TimerFinished() => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(Chime, writable: false);
                using var player = new SoundPlayer(stream);
                player.PlaySync();
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        });

        private static byte[] TimerChime()
        {
            const int rate = 44100;
            const double length = 1.1;
            int samples = (int)(rate * length);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8.ToArray()); w.Write(36 + samples * 2); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(samples * 2);
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / rate, value = 0;
                // Two quiet, rounded notes with a gentle attack and a decaying tail.
                for (int note = 0; note < 2; note++)
                {
                    double local = t - note * 0.24;
                    if (local < 0) continue;
                    double frequency = note == 0 ? 659.25 : 987.77;
                    double envelope = Math.Min(1, local / 0.025) * Math.Exp(-local * 5.5);
                    value += Math.Sin(2 * Math.PI * frequency * local) * envelope * 0.10;
                }
                value *= Math.Min(1, (length - t) / 0.08);
                w.Write((short)(value * short.MaxValue));
            }
            return ms.ToArray();
        }

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
