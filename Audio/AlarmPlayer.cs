using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    /// <summary>
    /// Looping phone-style alarm (bright marimba-like chirps), synthesized in code.
    /// Fades in over a few seconds; the effective level is capped at 60% of maximum and the
    /// system volume is never changed.
    /// </summary>
    public sealed class AlarmPlayer : IDisposable
    {
        private const double MaxEffectiveLevel = 0.6;
        private WasapiOut? output;

        public bool Playing => output != null;

        public void Start(string? outputDeviceId, float systemVolume)
        {
            Stop();
            var en = new MMDeviceEnumerator();
            var device = outputDeviceId != null ? en.GetDevice(outputDeviceId) : en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var format = device.AudioClient.MixFormat;
            float gain = (float)Math.Min(0.85, MaxEffectiveLevel / Math.Max(0.05, systemVolume));
            var o = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            o.Init(new LoopProvider(format, BuildLoop(format.SampleRate), gain));
            output = o;
            o.Play();
        }

        public void Stop()
        {
            var o = Interlocked.Exchange(ref output, null);
            if (o == null) return;
            try { o.Stop(); } catch { }
            o.Dispose();
        }

        public void Dispose() => Stop();

        /// <summary>One 2-second cycle: a quick rising-falling 6-note chirp figure twice, then a pause.</summary>
        private static float[] BuildLoop(int rate)
        {
            double[] figure = { 1568, 2093, 1568, 2093, 1760, 2349 }; // G6 C7 G6 C7 A6 D7
            double noteSec = 0.085, gapSec = 0.03;
            int len = (int)(rate * 2.0);
            var s = new float[len];
            double t0 = 0;
            for (int rep = 0; rep < 2; rep++)
            {
                foreach (double f in figure)
                {
                    int start = (int)(t0 * rate), n = (int)(noteSec * 2.2 * rate);
                    for (int i = 0; i < n && start + i < len; i++)
                    {
                        double t = (double)i / rate;
                        double env = Math.Min(1, t / 0.003) * Math.Exp(-t * 26); // marimba-like strike
                        double tone = Math.Sin(2 * Math.PI * f * t) + 0.35 * Math.Sin(2 * Math.PI * f * 3.9 * t) * Math.Exp(-t * 60);
                        s[start + i] += (float)(tone * env * 0.6);
                    }
                    t0 += noteSec + gapSec;
                }
                t0 += 0.12;
            }
            float max = 0;
            foreach (var v in s) max = Math.Max(max, Math.Abs(v));
            for (int i = 0; i < len; i++) s[i] /= max;
            return s;
        }

        private sealed class LoopProvider : IWaveProvider
        {
            private readonly float[] loop;
            private readonly int channels, rate;
            private readonly float gain;
            private long pos;
            public WaveFormat WaveFormat { get; }

            public LoopProvider(WaveFormat format, float[] loop, float gain)
            {
                WaveFormat = format;
                channels = format.Channels;
                rate = format.SampleRate;
                this.loop = loop;
                this.gain = gain;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                var f = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
                int frames = f.Length / channels;
                for (int i = 0; i < frames; i++, pos++)
                {
                    double fadeIn = 0.25 + 0.75 * Math.Min(1, pos / (rate * 6.0)); // gets louder over 6 s
                    float v = (float)(loop[pos % loop.Length] * gain * fadeIn);
                    for (int c = 0; c < channels; c++) f[i * channels + c] = c < 2 ? v : 0;
                }
                return count;
            }
        }
    }
}
