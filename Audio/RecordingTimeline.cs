using System;
using System.Diagnostics;
using System.Globalization;
using NAudio.Wave;

namespace GraphiteCompanion.Audio
{
    // PCM sample positions and video timestamps use the same session origin. Waiting for a
    // capture packet is transport latency, not silence to insert into the media timeline.
    internal sealed class RecordingClock
    {
        public const int Rate = 48000, ReserveMilliseconds = 80;
        public readonly long OriginQpc100ns;
        public readonly long OriginUnixMicroseconds;
        public RecordingClock()
        {
            long before = Stopwatch.GetTimestamp();
            OriginUnixMicroseconds = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
            long after = Stopwatch.GetTimestamp();
            OriginQpc100ns = (long)(((before + after) / 2.0) * 10_000_000 / Stopwatch.Frequency);
        }
        internal RecordingClock(long qpc100ns, long unixMicroseconds)
        { OriginQpc100ns = qpc100ns; OriginUnixMicroseconds = unixMicroseconds; }
        public static long Qpc100ns => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        public long SampleAt(long qpc100ns) => (long)Math.Round((qpc100ns - OriginQpc100ns) * (Rate / 10_000_000.0));
        public long ReadySamples => Math.Max(0, SampleAt(Qpc100ns) - Rate * ReserveMilliseconds / 1000);
        // RTCTIME is evaluated once, on the first video frame. Subsequent frames retain
        // ddagrab's monotonic relative PTS, so a later wall-clock adjustment cannot move them.
        internal string VideoTimestampFilter =>
            "setpts=PTS-STARTPTS+if(eq(N\\,0)\\,st(0\\,(RTCTIME-" +
            OriginUnixMicroseconds.ToString(CultureInfo.InvariantCulture) + ")/1000000)\\,ld(0))/TB";
    }

    internal sealed class RecordingTimeline
    {
        private const int Capacity = RecordingClock.Rate * 2;
        private readonly long[] positions = new long[Capacity];
        private readonly float[] left = new float[Capacity], right = new float[Capacity];
        private readonly RecordingClock clock;
        private readonly int channels, rate, bytes;
        private readonly bool floating;
        private long consumed;

        internal RecordingTimeline(WaveFormat format, RecordingClock clock)
        {
            this.clock = clock; channels = format.Channels; rate = format.SampleRate; bytes = format.BitsPerSample / 8;
            floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext &&
                ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (channels < 1 || rate < 1 || (floating ? bytes != 4 : bytes is < 1 or > 4))
                throw new NotSupportedException("Unsupported recording audio format: " + format);
            Array.Fill(positions, long.MinValue);
        }
        private float Sample(byte[] data, int at)
        {
            float value = floating ? BitConverter.ToSingle(data, at) : bytes switch
            {
                1 => (data[at] - 128) / 128f,
                2 => BitConverter.ToInt16(data, at) / 32768f,
                3 => ((data[at] | data[at + 1] << 8 | data[at + 2] << 16) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(data, at) / 2147483648f,
                _ => 0
            };
            return float.IsFinite(value) ? value : 0;
        }
        private (float l, float r) Stereo(byte[] data, int frame)
        {
            int at = frame * channels * bytes;
            float S(int ch) => Sample(data, at + ch * bytes);
            if (channels == 1) { float m = S(0); return (m, m); }
            float l = S(0), r = S(1); const float k = 0.7071f;
            if (channels == 4) return (l + S(2) * k, r + S(3) * k);
            if (channels >= 3) { float c = S(2) * k; l += c; r += c; }
            if (channels >= 4) { float bass = S(3) * 0.5f; l += bass; r += bass; }
            for (int ch = 4; ch + 1 < channels; ch += 2) { l += S(ch) * k; r += S(ch + 1) * k; }
            return (l, r);
        }
        // Packet timestamp is the WASAPI QPC position of its FIRST frame, not the time
        // its callback happened to run. Delayed delivery and silent gaps cannot accumulate delay.
        internal void Write(byte[] data, int count, long firstQpc100ns)
        {
            int frames = count / (channels * bytes);
            if (frames == 0) return;
            double start = (firstQpc100ns - clock.OriginQpc100ns) * (RecordingClock.Rate / 10_000_000.0);
            long first = (long)Math.Round(start), end = (long)Math.Round(start + frames * (double)RecordingClock.Rate / rate);
            lock (positions)
            {
                first = Math.Max(first, Math.Max(consumed, end - Capacity));
                end = Math.Min(end, consumed + Capacity);
                for (long index = first; index < end; index++)
                {
                    double position = Math.Clamp((index - start) * rate / RecordingClock.Rate, 0, frames - 1);
                    int a = (int)position, b = Math.Min(a + 1, frames - 1); float f = (float)(position - a);
                    var aa = Stereo(data, a); var bb = Stereo(data, b);
                    int slot = (int)(index % Capacity);
                    left[slot] = aa.l + (bb.l - aa.l) * f; right[slot] = aa.r + (bb.r - aa.r) * f; positions[slot] = index;
                }
            }
        }
        internal (float l, float r) Read(long sample)
        {
            lock (positions)
            {
                consumed = Math.Max(consumed, sample + 1);
                int slot = (int)(sample % Capacity);
                return positions[slot] == sample ? (left[slot], right[slot]) : (0, 0);
            }
        }
    }
}
