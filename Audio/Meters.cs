using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    /// <summary>Real-time spectrum of what is playing on an output device (loopback capture).</summary>
    public sealed class SpectrumAnalyzer : IDisposable
    {
        public const int Bars = 56;
        private const int FftSize = 4096, FftPow = 12;

        private WasapiLoopbackCapture? capture;
        private readonly float[] ring = new float[FftSize];
        private int ringPos;
        private readonly object gate = new();
        private readonly float[] window = new float[FftSize];
        private readonly Complex[] fft = new Complex[FftSize];
        private int sampleRate = 48000;

        public SpectrumAnalyzer()
        {
            for (int i = 0; i < FftSize; i++) window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
        }

        public bool Running => capture != null;

        public void Start(string outputDeviceId)
        {
            Stop();
            var device = new MMDeviceEnumerator().GetDevice(outputDeviceId);
            var c = new WasapiLoopbackCapture(device);
            var fmt = c.WaveFormat;
            sampleRate = fmt.SampleRate;
            int channels = fmt.Channels;
            bool isFloat = fmt.BitsPerSample == 32;
            c.DataAvailable += (_, e) =>
            {
                int bps = fmt.BitsPerSample / 8, frame = bps * channels;
                lock (gate)
                {
                    for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
                    {
                        // mix front left + right
                        float l = isFloat ? BitConverter.ToSingle(e.Buffer, i) : BitConverter.ToInt16(e.Buffer, i) / 32768f;
                        float r = channels > 1
                            ? (isFloat ? BitConverter.ToSingle(e.Buffer, i + bps) : BitConverter.ToInt16(e.Buffer, i + bps) / 32768f)
                            : l;
                        ring[ringPos] = (l + r) * 0.5f;
                        ringPos = (ringPos + 1) % FftSize;
                    }
                }
            };
            capture = c;
            c.StartRecording();
        }

        /// <summary>Fills <paramref name="levels"/> (0..1, log-spaced 30 Hz – 18 kHz).</summary>
        public void GetLevels(float[] levels)
        {
            lock (gate)
            {
                for (int i = 0; i < FftSize; i++)
                {
                    fft[i].X = ring[(ringPos + i) % FftSize] * window[i];
                    fft[i].Y = 0;
                }
            }
            FastFourierTransform.FFT(true, FftPow, fft);
            double fMin = 30, fMax = 18000;
            for (int b = 0; b < levels.Length; b++)
            {
                double f0 = fMin * Math.Pow(fMax / fMin, (double)b / levels.Length);
                double f1 = fMin * Math.Pow(fMax / fMin, (double)(b + 1) / levels.Length);
                int i0 = Math.Max(1, (int)(f0 * FftSize / sampleRate));
                int i1 = Math.Max(i0 + 1, (int)(f1 * FftSize / sampleRate));
                double peak = 0;
                for (int i = i0; i < i1 && i < FftSize / 2; i++)
                    peak = Math.Max(peak, Math.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y));
                double db = 20 * Math.Log10(peak + 1e-9) + 6; // roughly -84..0 dB
                levels[b] = (float)Math.Clamp((db + 84) / 84, 0, 1);
            }
        }

        public void Stop()
        {
            var c = Interlocked.Exchange(ref capture, null);
            if (c == null) return;
            try { c.StopRecording(); } catch { }
            c.Dispose();
            lock (gate) Array.Clear(ring);
        }

        public void Dispose() => Stop();
    }

    /// <summary>Peak level of a microphone (after the driver's noise reduction).</summary>
    public sealed class MicLevelMeter : IDisposable
    {
        private WasapiCapture? capture;
        private float peak;

        public bool Running => capture != null;

        public void Start(string micDeviceId)
        {
            Stop();
            var device = new MMDeviceEnumerator().GetDevice(micDeviceId);
            var c = new WasapiCapture(device, true, 30);
            var fmt = c.WaveFormat;
            bool isFloat = fmt.BitsPerSample == 32;
            int bps = fmt.BitsPerSample / 8;
            c.DataAvailable += (_, e) =>
            {
                float p = 0;
                for (int i = 0; i + bps <= e.BytesRecorded; i += bps)
                {
                    float v = isFloat ? BitConverter.ToSingle(e.Buffer, i) : BitConverter.ToInt16(e.Buffer, i) / 32768f;
                    p = Math.Max(p, Math.Abs(v));
                }
                if (p > peak) peak = p;
            };
            capture = c;
            c.StartRecording();
        }

        /// <summary>Returns the peak since the last call in dBFS (-90..0).</summary>
        public double TakePeakDb()
        {
            float p = Interlocked.Exchange(ref peak, 0);
            return Math.Max(-90, 20 * Math.Log10(p + 1e-9));
        }

        public void Stop()
        {
            var c = Interlocked.Exchange(ref capture, null);
            if (c == null) return;
            try { c.StopRecording(); } catch { }
            c.Dispose();
        }

        public void Dispose() => Stop();
    }

    /// <summary>Continuous sine tone with adjustable frequency and level (for the hearing test).</summary>
    public sealed class TonePlayer : IDisposable
    {
        private WasapiOut? output;
        private ToneProvider? provider;

        public void Start(string outputDeviceId)
        {
            Stop();
            var device = new MMDeviceEnumerator().GetDevice(outputDeviceId);
            provider = new ToneProvider(device.AudioClient.MixFormat);
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            output.Init(provider);
            output.Play();
        }

        /// <summary>Sets the tone. dbfs = null means silence.</summary>
        public void Set(double frequency, double? dbfs)
        {
            if (provider == null) return;
            provider.Frequency = frequency;
            provider.TargetAmp = dbfs.HasValue ? Math.Pow(10, dbfs.Value / 20) : 0;
        }

        public void Stop()
        {
            var o = Interlocked.Exchange(ref output, null);
            provider = null;
            if (o == null) return;
            try { o.Stop(); } catch { }
            o.Dispose();
        }

        public void Dispose() => Stop();

        private sealed class ToneProvider : IWaveProvider
        {
            private readonly int channels, rate;
            private double phase, amp;
            public volatile float FrequencyF = 1000;
            public double Frequency { get => FrequencyF; set => FrequencyF = (float)value; }
            public double TargetAmp;
            public WaveFormat WaveFormat { get; }

            public ToneProvider(WaveFormat format)
            {
                WaveFormat = format;
                channels = format.Channels;
                rate = format.SampleRate;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
                int frames = floats.Length / channels;
                double step = 2 * Math.PI * FrequencyF / rate;
                double target = TargetAmp;
                double smooth = 1.0 / (rate * 0.03); // ~30 ms ramps, no clicks
                for (int f = 0; f < frames; f++)
                {
                    amp += (target - amp) * smooth * 4;
                    float v = (float)(Math.Sin(phase) * amp);
                    phase += step;
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                    for (int c = 0; c < channels; c++) floats[f * channels + c] = c < 2 ? v : 0;
                }
                return count;
            }
        }
    }
}
