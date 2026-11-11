using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    /// <summary>
    /// Produces one real-time 48 kHz stereo 16-bit PCM stream for the recorder: system sound (loopback of
    /// the Windows default output) mixed with the microphone. WASAPI loopback delivers nothing while
    /// nothing plays, so a pacing thread always writes exactly real-time worth of samples (silence where
    /// needed) - this keeps audio in sync with the video.
    /// </summary>
    public sealed class RecordingAudio : IDisposable
    {
        private const int Rate = 48000;

        private sealed class Source : IDisposable
        {
            private readonly IWaveIn capture;
            private readonly int channels, srcRate;
            private readonly bool isFloat;
            private readonly int bps;
            private readonly Queue<(float l, float r)> queue = new();
            public volatile bool Failed;
            private double resamplePos;
            private (float l, float r) prev;
            public readonly float Gain;

            public Source(IWaveIn capture, float gain)
            {
                this.capture = capture;
                Gain = gain;
                var f = capture.WaveFormat;
                channels = f.Channels; srcRate = f.SampleRate;
                isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat ||
                    f is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
                bps = f.BitsPerSample / 8;
                if (channels < 1 || srcRate < 1 || (isFloat ? bps != 4 : bps is < 1 or > 4))
                    throw new NotSupportedException("Unsupported recording audio format: " + f);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null) { Failed = true; GraphiteCompanion.Services.SettingsStore.LogError(e.Exception); }
                };
                capture.StartRecording();
            }

            private float Sample(byte[] b, int at)
            {
                float value = isFloat ? BitConverter.ToSingle(b, at) : bps switch
                {
                    1 => (b[at] - 128) / 128f,
                    2 => BitConverter.ToInt16(b, at) / 32768f,
                    3 => ((b[at] | b[at + 1] << 8 | b[at + 2] << 16) << 8 >> 8) / 8388608f,
                    4 => BitConverter.ToInt32(b, at) / 2147483648f,
                    _ => 0
                };
                return float.IsFinite(value) ? value : 0;
            }

            /// <summary>
            /// Any layout → stereo. Headsets with virtual 7.1 make Windows mix in 8 channels
            /// (FL FR FC LFE BL BR SL SR); only taking the front pair lost everything behind / beside the player.
            /// </summary>
            private (float l, float r) Downmix(byte[] b, int at)
            {
                float S(int ch) => Sample(b, at + ch * bps);
                if (channels == 1) { float m = S(0); return (m, m); }
                float l = S(0), r = S(1);
                const float k = 0.7071f;
                if (channels == 4) { l += S(2) * k; r += S(3) * k; return (l, r); }   // quad: FL FR BL BR
                if (channels >= 3) { float c = S(2) * k; l += c; r += c; }
                if (channels >= 4) { float lfe = S(3) * 0.5f; l += lfe; r += lfe; }
                for (int ch = 4; ch + 1 < channels; ch += 2) { l += S(ch) * k; r += S(ch + 1) * k; } // back, side pairs
                return (l, r);
            }

            private void OnData(object? s, WaveInEventArgs e)
            {
                int frame = bps * channels;
                lock (queue)
                {
                    for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
                    {
                        var (l, r) = Downmix(e.Buffer, i);
                        // linear resample to 48 kHz
                        double step = (double)srcRate / Rate;
                        while (resamplePos <= 1)
                        {
                            queue.Enqueue((prev.l + (l - prev.l) * (float)resamplePos, prev.r + (r - prev.r) * (float)resamplePos));
                            resamplePos += step;
                        }
                        resamplePos -= 1;
                        prev = (l, r);
                    }
                    while (queue.Count > Rate) queue.Dequeue(); // never keep more than 1 s (drift guard)
                }
            }

            // Jitter buffer. Devices deliver in 10–20 ms packets and the pump runs on a ~16 ms timer, so reading
            // the queue dry the moment it has data made the microphone stutter (dozens of 16/32/48 ms gaps of
            // silence per recording). Keep ~80 ms in reserve and nudge the level by one sample in 400 when the
            // device clock runs slightly faster or slower than the wall clock - inaudible, no clicks.
            private const int Prime = Rate * 8 / 100, Low = Prime / 2, High = Prime * 3; // 80 ms: survives a capture thread that a game delays by ~60 ms
            private bool primed;
            private int tick;

            public (float l, float r) Next()
            {
                lock (queue)
                {
                    if (!primed)
                    {
                        if (queue.Count < Prime) return (0f, 0f);   // (re)filling the reserve
                        primed = true;
                    }
                    if (queue.Count == 0) { primed = false; return (0f, 0f); } // source went quiet (loopback with nothing playing)
                    if (queue.Count > Rate / 2) while (queue.Count > Prime * 2) queue.Dequeue(); // long stall: catch up at once
                    if (++tick % 400 == 0)
                    {
                        if (queue.Count > High) queue.Dequeue();        // a little behind: play one sample less
                        else if (queue.Count < Low) return queue.Peek(); // running dry: repeat one sample
                    }
                    return queue.Dequeue();
                }
            }

            public void Dispose()
            {
                try { capture.StopRecording(); } catch { }
                capture.Dispose();
            }
        }

        /// <summary>
        /// System sound capture with a 20 ms buffer. NAudio's WasapiLoopbackCapture uses 100 ms and reads it every
        /// 50 ms - as long as the whole jitter reserve, so a capture thread delayed by a busy game left gaps.
        /// </summary>
        private sealed class LoopbackCapture : WasapiCapture
        {
            public LoopbackCapture(MMDevice device) : base(device, false, 20) { }
            protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback;
        }

        /// <summary>
        /// One ffmpeg stdin. Its own thread writes to it, so an ffmpeg that is slow for a moment (it runs below normal
        /// priority next to a game) no longer blocks the mixer - which then had to throw sound away to catch up -
        /// nor the other ffmpeg (replay buffer and manual recording share the stream).
        /// </summary>
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

        private readonly List<Source> sources = new();
        private readonly List<Output> outputs = new();
        private readonly Thread pump;
        private volatile bool running = true;
        public bool Failed => sources.Exists(s => s.Failed);

        /// <summary>For tests: mix the given captures instead of the Windows devices.</summary>
        public RecordingAudio(IEnumerable<IWaveIn> captures)
        {
            foreach (var c in captures) sources.Add(new Source(c, 1f));
            pump = new Thread(Pump) { IsBackground = true, Name = "RecordingAudio", Priority = ThreadPriority.AboveNormal };
            pump.Start();
        }

        public RecordingAudio(bool systemSound, bool microphone)
        {
            using var en = new MMDeviceEnumerator();
            if (systemSound)
            {
                try { sources.Add(new Source(new LoopbackCapture(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)), 1f)); }
                catch (Exception ex) { GraphiteCompanion.Services.SettingsStore.LogError(ex); }
            }
            if (microphone)
            {
                try { sources.Add(new Source(new WasapiCapture(en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications), true, 20), 1f)); }
                catch (Exception ex) { GraphiteCompanion.Services.SettingsStore.LogError(ex); }
            }
            pump = new Thread(Pump) { IsBackground = true, Name = "RecordingAudio", Priority = ThreadPriority.AboveNormal };
            pump.Start();
        }

        /// <summary>ffmpeg stdin to feed; removed automatically when the process goes away.</summary>
        public void AddOutput(Stream s) { lock (outputs) outputs.Add(new Output(s)); }

        public void RemoveOutput(Stream s)
        {
            lock (outputs)
            {
                foreach (var o in outputs.FindAll(o => o.Stream == s)) o.Close();
                outputs.RemoveAll(o => o.Stream == s);
            }
        }

        private void Pump()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long written = 0;
            var buf = new byte[Rate / 10 * 4]; // up to 100 ms per write
            while (running)
            {
                Thread.Sleep(10);
                long due = (long)(clock.Elapsed.TotalSeconds * Rate) - written;
                if (due <= 0) continue;
                if (due > Rate / 10) due = Rate / 10;
                for (int i = 0; i < due; i++)
                {
                    float l = 0, r = 0;
                    foreach (var s in sources) { var v = s.Next(); l += v.l * s.Gain; r += v.r * s.Gain; }
                    short L = (short)(Math.Clamp(l, -1f, 1f) * 32767), R = (short)(Math.Clamp(r, -1f, 1f) * 32767);
                    buf[i * 4] = (byte)L; buf[i * 4 + 1] = (byte)(L >> 8);
                    buf[i * 4 + 2] = (byte)R; buf[i * 4 + 3] = (byte)(R >> 8);
                }
                written += due;
                var chunk = buf.AsSpan(0, (int)due * 4).ToArray();
                lock (outputs)
                {
                    foreach (var o in outputs) o.Post(chunk);
                    foreach (var o in outputs.FindAll(o => o.Dead)) o.Close();
                    outputs.RemoveAll(o => o.Dead);
                }
            }
        }

        public void Dispose()
        {
            running = false;
            pump.Join(500);
            lock (outputs) { foreach (var o in outputs) o.Close(); outputs.Clear(); }
            foreach (var s in sources) s.Dispose();
            sources.Clear();
        }
    }
}
