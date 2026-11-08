using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    /// <summary>Speaker channel test and "hear yourself" microphone monitoring.</summary>
    public sealed class AudioTests : IDisposable
    {
        private WasapiOut? channelOut;
        private WasapiOut? monitorOut;
        private WasapiCapture? monitorIn;

        public bool ChannelTestRunning => channelOut != null;
        public bool MonitorRunning => monitorOut != null;

        public static readonly string[] ChannelNames8 =
            { "Левый фронт", "Правый фронт", "Центр", "Сабвуфер", "Левый тыл", "Правый тыл", "Левый бок", "Правый бок" };

        // ---------------- channel test ----------------

        /// <summary>Plays a short noise burst on every channel in turn. Callback gets the channel name (null at the end).</summary>
        public void StartChannelTest(string outputDeviceId, Action<string?> onChannel)
        {
            StopChannelTest();
            var device = new MMDeviceEnumerator().GetDevice(outputDeviceId);
            var format = device.AudioClient.MixFormat;
            var provider = new ChannelTestProvider(format, onChannel);
            channelOut = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            channelOut.Init(provider);
            provider.Finished += () => ThreadPool.QueueUserWorkItem(_ => StopChannelTest());
            channelOut.Play();
        }

        public void StopChannelTest()
        {
            var o = Interlocked.Exchange(ref channelOut, null);
            if (o == null) return;
            try { o.Stop(); } catch { }
            o.Dispose();
        }

        private sealed class ChannelTestProvider : IWaveProvider
        {
            private const double BurstSec = 1.0, GapSec = 0.25;
            private readonly int channels, rate, burst, gap;
            private readonly Action<string?> onChannel;
            private readonly Random rng = new();
            private long position;
            private int lastReported = -1;
            private double b0, b1, b2;
            public event Action? Finished;
            public WaveFormat WaveFormat { get; }

            public ChannelTestProvider(WaveFormat format, Action<string?> onChannel)
            {
                WaveFormat = format;
                channels = format.Channels;
                rate = format.SampleRate;
                burst = (int)(rate * BurstSec);
                gap = (int)(rate * GapSec);
                this.onChannel = onChannel;
            }

            private string NameOf(int ch) =>
                channels == 2 ? (ch == 0 ? "Левый" : "Правый")
                : ch < ChannelNames8.Length ? ChannelNames8[ch] : $"Канал {ch + 1}";

            public int Read(byte[] buffer, int offset, int count)
            {
                var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
                int frames = floats.Length / channels;
                int step = burst + gap;
                long total = (long)step * channels;
                for (int f = 0; f < frames; f++, position++)
                {
                    int ch = (int)(position / step);
                    int inStep = (int)(position % step);
                    for (int c = 0; c < channels; c++) floats[f * channels + c] = 0;
                    if (position >= total) continue;
                    if (ch != lastReported) { lastReported = ch; onChannel(NameOf(ch)); }
                    if (inStep >= burst) continue;
                    // pink-ish noise with a soft envelope
                    double white = rng.NextDouble() * 2 - 1;
                    b0 = 0.99765 * b0 + white * 0.0990460;
                    b1 = 0.96300 * b1 + white * 0.2965164;
                    b2 = 0.57000 * b2 + white * 1.0526913;
                    double pink = (b0 + b1 + b2 + white * 0.1848) * 0.11;
                    double env = Math.Min(1.0, Math.Min(inStep, burst - inStep) / (rate * 0.03));
                    floats[f * channels + ch] = (float)(pink * env * 0.5);
                }
                if (position >= total && lastReported != -2)
                {
                    lastReported = -2;
                    onChannel(null);
                    Finished?.Invoke();
                }
                return count;
            }
        }

        // ---------------- microphone monitor ----------------

        public void StartMonitor(string micDeviceId, string outputDeviceId)
        {
            StopMonitor();
            var en = new MMDeviceEnumerator();
            var mic = en.GetDevice(micDeviceId);
            var output = en.GetDevice(outputDeviceId);
            var outFormat = output.AudioClient.MixFormat;
            var ring = new MonoRing(outFormat.SampleRate / 2);

            monitorIn = new WasapiCapture(mic, true, 20);
            var inFormat = monitorIn.WaveFormat;
            var resampler = new LinearResampler(inFormat.SampleRate, outFormat.SampleRate);
            monitorIn.DataAvailable += (_, e) =>
            {
                int bytesPerSample = inFormat.BitsPerSample / 8;
                int frameBytes = bytesPerSample * inFormat.Channels;
                for (int i = 0; i + frameBytes <= e.BytesRecorded; i += frameBytes)
                {
                    double sum = 0;
                    for (int c = 0; c < inFormat.Channels; c++)
                    {
                        int at = i + c * bytesPerSample;
                        sum += bytesPerSample == 4
                            ? BitConverter.ToSingle(e.Buffer, at)
                            : BitConverter.ToInt16(e.Buffer, at) / 32768.0;
                    }
                    resampler.Push((float)(sum / inFormat.Channels), ring.Write);
                }
            };

            monitorOut = new WasapiOut(output, AudioClientShareMode.Shared, true, 30);
            monitorOut.Init(new MonitorProvider(outFormat, ring));
            monitorIn.StartRecording();
            monitorOut.Play();
        }

        public void StopMonitor()
        {
            var i = Interlocked.Exchange(ref monitorIn, null);
            var o = Interlocked.Exchange(ref monitorOut, null);
            try { i?.StopRecording(); } catch { }
            i?.Dispose();
            try { o?.Stop(); } catch { }
            o?.Dispose();
        }

        private sealed class MonoRing
        {
            private readonly float[] data;
            private int read, write, count;
            private readonly object gate = new();
            public MonoRing(int capacity) => data = new float[capacity];

            public void Write(float v)
            {
                lock (gate)
                {
                    data[write] = v;
                    write = (write + 1) % data.Length;
                    if (count < data.Length) count++;
                    else read = (read + 1) % data.Length;
                    // keep latency low: drop old audio if more than ~80 ms is queued
                    int max = data.Length / 6;
                    while (count > max) { read = (read + 1) % data.Length; count--; }
                }
            }

            public float Read()
            {
                lock (gate)
                {
                    if (count == 0) return 0;
                    float v = data[read];
                    read = (read + 1) % data.Length;
                    count--;
                    return v;
                }
            }
        }

        private sealed class LinearResampler
        {
            private readonly double ratio;
            private double t;
            private float prev;
            public LinearResampler(int inRate, int outRate) => ratio = (double)inRate / outRate;

            public void Push(float sample, Action<float> emit)
            {
                if (Math.Abs(ratio - 1) < 1e-9) { emit(sample); return; }
                while (t <= 1)
                {
                    emit((float)(prev + (sample - prev) * t));
                    t += ratio;
                }
                t -= 1;
                prev = sample;
            }
        }

        private sealed class MonitorProvider : IWaveProvider
        {
            private readonly MonoRing ring;
            private readonly int channels;
            public WaveFormat WaveFormat { get; }

            public MonitorProvider(WaveFormat format, MonoRing ring)
            {
                WaveFormat = format;
                channels = format.Channels;
                this.ring = ring;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                var floats = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
                int frames = floats.Length / channels;
                for (int f = 0; f < frames; f++)
                {
                    float v = ring.Read();
                    for (int c = 0; c < channels; c++) floats[f * channels + c] = c < 2 ? v : 0;
                }
                return count;
            }
        }

        public void Dispose()
        {
            StopChannelTest();
            StopMonitor();
        }
    }
}
