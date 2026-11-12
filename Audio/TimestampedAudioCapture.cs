using System;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    // NAudio's IWaveIn event omits WASAPI timestamps. Preserve GetBuffer's QPC
    // position so a scheduling delay is not mistaken for late audio content.
    internal sealed class TimestampedAudioCapture : IDisposable
    {
        private readonly MMDevice device;
        private readonly AudioClient client;
        private readonly Thread thread;
        private readonly Action<byte[], int, long> packet;
        private readonly Action<Exception> failed;
        private volatile bool running = true;
        public WaveFormat Format { get; }

        internal TimestampedAudioCapture(MMDevice device, bool loopback,
            Func<WaveFormat, Action<byte[], int, long>> makeSink, Action<Exception> failed)
        {
            this.device = device; this.failed = failed;
            client = device.AudioClient;
            try
            {
                Format = client.MixFormat;
                packet = makeSink(Format);
                client.Initialize(AudioClientShareMode.Shared,
                    loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None,
                    200_000, 0, Format, Guid.Empty);
                // Start synchronously: an unavailable source must fail before reporting a recording.
                client.Start();
                thread = new Thread(Run) { IsBackground = true, Name = "RecordingAudio.Capture", Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
            catch { try { client.Stop(); } catch { } client.Dispose(); throw; }
        }
        private void Run()
        {
            long anchorPosition = 0, anchorQpc = 0;
            try
            {
                using var capture = client.AudioCaptureClient;
                while (running)
                {
                    Thread.Sleep(5);
                    while (running && capture.GetNextPacketSize() > 0)
                    {
                        IntPtr pointer = capture.GetBuffer(out int frames, out var flags, out long position, out long qpc);
                        if (frames == 0) break;
                        var data = new byte[checked(frames * Format.BlockAlign)];
                        try
                        {
                            if ((flags & AudioClientBufferFlags.Silent) == 0) Marshal.Copy(pointer, data, 0, data.Length);
                        }
                        finally { capture.ReleaseBuffer(frames); }
                        if ((flags & AudioClientBufferFlags.TimestampError) != 0 || qpc <= 0)
                            qpc = anchorQpc != 0 ? anchorQpc + (long)((position - anchorPosition) * (10_000_000.0 / Format.SampleRate))
                                : RecordingClock.Qpc100ns - (long)(frames * (10_000_000.0 / Format.SampleRate));
                        else { anchorQpc = qpc; anchorPosition = position; }
                        packet(data, data.Length, qpc);
                    }
                }
            }
            catch (Exception ex) { if (running) failed(ex); }
            finally { try { client.Stop(); } catch { } }
        }
        public void Dispose()
        {
            running = false;
            thread.Join();
            client.Dispose(); device.Dispose();
        }
    }
}
