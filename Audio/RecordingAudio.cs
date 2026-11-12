using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    /// <summary>Timestamped 48 kHz stereo PCM. A short delivery reserve does not shift media time.</summary>
    public sealed class RecordingAudio : IDisposable
    {
        private sealed class Output
        {
            public readonly Stream Stream;
            private readonly System.Collections.Concurrent.BlockingCollection<byte[]> queue = new(boundedCapacity: 600); // ~10 s
            public volatile bool Dead;

            public Output(Stream s)
            {
                Stream = s;
                new Thread(Run) { IsBackground = true, Name = "RecordingAudio.Out", Priority = ThreadPriority.AboveNormal }.Start();
            }

            public void Post(byte[] data)
            {
                if (Dead || queue.IsAddingCompleted) return;
                if (!queue.TryAdd(data))
                {
                    // Do not silently abandon an OPEN PCM pipe: FFmpeg then waits
                    // forever for audio while retaining video frames in memory.
                    Dead = true;
                    Close();
                    ThreadPool.QueueUserWorkItem(_ => { try { Stream.Dispose(); } catch { } });
                }
            }

            public void Close() { try { queue.CompleteAdding(); } catch { } }

            private void Run()
            {
                try
                {
                    foreach (var d in queue.GetConsumingEnumerable())
                    {
                        Stream.Write(d, 0, d.Length);
                        Stream.Flush();
                    }
                }
                catch { Dead = true; } // ffmpeg exited
                finally { try { Stream.Dispose(); } catch { } }
            }
        }

        private readonly RecordingClock clock = new();
        private readonly List<RecordingTimeline> sources = new();
        private readonly List<IDisposable> captures = new();
        private readonly List<Output> outputs = new();
        private readonly Thread pump;
        private volatile bool running = true, failed;
        public bool Failed => failed;
        internal RecordingClock Clock => clock;

        internal RecordingAudio(RecordingClock clock, IEnumerable<RecordingTimeline> timelines)
        {
            this.clock = clock; sources.AddRange(timelines);
            pump = NewPump(); pump.Start();
        }

        // Kept for device-free regression tests. Production captures preserve native QPC timestamps.
        public RecordingAudio(IEnumerable<IWaveIn> input)
        {
            try
            {
                foreach (var capture in input)
                {
                    var timeline = new RecordingTimeline(capture.WaveFormat, clock); sources.Add(timeline);
                    capture.DataAvailable += (_, e) => timeline.Write(e.Buffer, e.BytesRecorded,
                        RecordingClock.Qpc100ns - (long)(e.BytesRecorded / (double)capture.WaveFormat.AverageBytesPerSecond * 10_000_000));
                    capture.RecordingStopped += (_, e) => { if (e.Exception != null) Fail(e.Exception); };
                    captures.Add(capture); capture.StartRecording();
                }
                pump = NewPump(); pump.Start();
            }
            catch { foreach (var capture in captures) capture.Dispose(); throw; }
        }

        public RecordingAudio(bool systemSound, bool microphone, string? microphoneDeviceId = null)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                if (systemSound) AddCapture(() => en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia), true,
                    "Не удалось открыть системный звук для записи. Проверьте устройство вывода Windows.");
                if (microphone) AddCapture(() => string.IsNullOrEmpty(microphoneDeviceId)
                        ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : en.GetDevice(microphoneDeviceId), false,
                    "Не удалось открыть выбранный микрофон для записи. Подключите его или выберите другой в разделе записи экрана.");
                pump = NewPump(); pump.Start();
            }
            catch { foreach (var capture in captures) capture.Dispose(); throw; }
        }
        private void AddCapture(Func<MMDevice> getDevice, bool loopback, string error)
        {
            MMDevice? device = null;
            try
            {
                device = getDevice();
                if (device.State != DeviceState.Active) throw new InvalidOperationException("Устройство отключено");
                var capture = new TimestampedAudioCapture(device, loopback, format =>
                {
                    var timeline = new RecordingTimeline(format, clock); sources.Add(timeline); return timeline.Write;
                }, Fail);
                captures.Add(capture); device = null;
            }
            catch (Exception ex) { device?.Dispose(); throw new IOException(error, ex); }
        }
        private void Fail(Exception ex) { failed = true; GraphiteCompanion.Services.SettingsStore.LogError(ex); }
        private Thread NewPump() => new(Pump) { IsBackground = true, Name = "RecordingAudio", Priority = ThreadPriority.AboveNormal };
        public void AddOutput(Stream stream) { lock (outputs) outputs.Add(new Output(stream)); }
        public void RemoveOutput(Stream stream)
        {
            lock (outputs)
            {
                foreach (var o in outputs.FindAll(o => o.Stream == stream)) o.Close();
                outputs.RemoveAll(o => o.Stream == stream);
            }
        }
        private void Pump()
        {
            long written = 0;
            var buffer = new byte[RecordingClock.Rate / 10 * 4];
            while (running)
            {
                Thread.Sleep(5);
                lock (outputs) { if (outputs.Count == 0) continue; }
                long due = Math.Min(clock.ReadySamples - written, RecordingClock.Rate / 10);
                if (due <= 0) continue;
                for (int i = 0; i < due; i++)
                {
                    float left = 0, right = 0;
                    foreach (var source in sources) { var value = source.Read(written + i); left += value.l; right += value.r; }
                    short l = (short)(Math.Clamp(left, -1f, 1f) * 32767), r = (short)(Math.Clamp(right, -1f, 1f) * 32767);
                    buffer[i * 4] = (byte)l; buffer[i * 4 + 1] = (byte)(l >> 8);
                    buffer[i * 4 + 2] = (byte)r; buffer[i * 4 + 3] = (byte)(r >> 8);
                }
                written += due;
                var chunk = buffer.AsSpan(0, (int)due * 4).ToArray();
                lock (outputs)
                {
                    foreach (var output in outputs) output.Post(chunk);
                    if (outputs.Exists(output => output.Dead)) failed = true;
                    foreach (var output in outputs.FindAll(output => output.Dead)) output.Close();
                    outputs.RemoveAll(output => output.Dead);
                }
            }
        }
        public void Dispose()
        {
            running = false; pump.Join();
            lock (outputs) { foreach (var output in outputs) output.Close(); outputs.Clear(); }
            foreach (var capture in captures) capture.Dispose();
        }
    }
}
