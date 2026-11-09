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
            private double resamplePos;
            private (float l, float r) prev;
            public readonly float Gain;

            public Source(IWaveIn capture, float gain)
            {
                this.capture = capture;
                Gain = gain;
                var f = capture.WaveFormat;
                channels = f.Channels; srcRate = f.SampleRate; isFloat = f.BitsPerSample == 32; bps = f.BitsPerSample / 8;
                capture.DataAvailable += OnData;
                capture.StartRecording();
            }

            private float Sample(byte[] b, int at) => isFloat ? BitConverter.ToSingle(b, at) : BitConverter.ToInt16(b, at) / 32768f;

            private void OnData(object? s, WaveInEventArgs e)
            {
                int frame = bps * channels;
                lock (queue)
                {
                    for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
                    {
                        float l = Sample(e.Buffer, i), r = channels > 1 ? Sample(e.Buffer, i + bps) : l;
                        if (channels > 2) { float c = Sample(e.Buffer, i + 2 * bps) * 0.7f; l += c; r += c; } // fold centre in
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

            public (float l, float r) Next()
            {
                lock (queue) return queue.Count > 0 ? queue.Dequeue() : (0f, 0f);
            }

            public void Dispose()
            {
                try { capture.StopRecording(); } catch { }
                capture.Dispose();
            }
        }

        private readonly List<Source> sources = new();
        private readonly List<Stream> outputs = new();
        private readonly Thread pump;
        private volatile bool running = true;

        public RecordingAudio(bool systemSound, bool microphone)
        {
            var en = new MMDeviceEnumerator();
            if (systemSound)
            {
                try { sources.Add(new Source(new WasapiLoopbackCapture(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)), 1f)); }
                catch { }
            }
            if (microphone)
            {
                try { sources.Add(new Source(new WasapiCapture(en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications), true, 20), 1f)); }
                catch { }
            }
            pump = new Thread(Pump) { IsBackground = true, Name = "RecordingAudio", Priority = ThreadPriority.AboveNormal };
            pump.Start();
        }

        /// <summary>ffmpeg stdin to feed; removed automatically when the process goes away.</summary>
        public void AddOutput(Stream s) { lock (outputs) outputs.Add(s); }
        public void RemoveOutput(Stream s) { lock (outputs) outputs.Remove(s); }

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
                Stream[] outs;
                lock (outputs) outs = outputs.ToArray();
                foreach (var o in outs)
                {
                    try { o.Write(buf, 0, (int)due * 4); o.Flush(); }
                    catch { RemoveOutput(o); } // ffmpeg exited
                }
            }
        }

        public void Dispose()
        {
            running = false;
            pump.Join(500);
            foreach (var s in sources) s.Dispose();
            sources.Clear();
        }
    }
}
