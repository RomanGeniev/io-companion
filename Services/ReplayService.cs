using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GraphiteCompanion.Audio;

namespace GraphiteCompanion.Services
{
    public sealed record RecordOptions
    {
        public IntPtr Monitor;            // HMONITOR to capture
        internal int CaptureAdapter, CaptureOutput;
        public Rectangle MonitorBounds;
        public int RequestedHeight;       // 0 = native
        public int Height => RequestedHeight < MonitorBounds.Height ? RequestedHeight : 0;
        public int Width => Height == 0 ? 0 : (int)Math.Round(Height * (double)MonitorBounds.Width / MonitorBounds.Height / 2) * 2;
        public int Fps = 60;
        public int Quality = 1;           // 0 low, 1 medium, 2 high
        public bool SystemSound = true;
        public bool Microphone;
        public string? MicrophoneDeviceId;
        public bool HasAudio => SystemSound || Microphone;
        public int ReplaySeconds = 60;
        public string Folder = "";
    }

    /// <summary>
    /// Screen recording through FFmpeg with the GPU's own encoder (NVENC / AMF / Quick Sync, x264 as last resort).
    /// Replay buffer: FFmpeg keeps writing short segments into a ring in %LOCALAPPDATA%; saving copies the last
    /// N seconds and joins them into one mp4 without re-encoding. Manual recording: a second FFmpeg writes a
    /// fragmented mp4 straight into the target folder (safe even if the PC crashes), finalised on stop.
    /// </summary>
    public sealed class ReplayService : IDisposable, IAsyncDisposable
    {
        public enum Encoder { None, Nvenc, Amf, Qsv, X264 }

        private const int SegmentSeconds = 1;
        private readonly string RingDir;
        private readonly string logSuffix;
        private static Encoder detected;
        private static readonly SemaphoreSlim folderOrganizationGate = new(1, 1);

        /// <summary>One service records one monitor; <paramref name="id"/> keeps the ring buffers of several monitors apart.</summary>
        public ReplayService(string id)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            RingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "GraphiteCompanion", "replay-buffer", id);
            logSuffix = id;
        }

        /// <summary>Top folder of this monitor inside the recordings folder, e.g. "Монитор 2".</summary>
        public string MonitorName { get; set; } = "Монитор 1";

        private readonly JobObject job = new();
        private RecordingAudio? bufferAudio, recordAudio;
        private Process? bufferProc, recordProc;
        private CaptureProgress? bufferProgress, recordProgress;
        private ReplayRing? activeRing, previousRing;
        private bool ringInitialized;
        private string? recordTemp, recordRoot;
        private DateTime recordStarted;
        private bool recordHasAudio, disposalStarted;
        private Task? recordStopTask;
        private long cachedBufferBytes, bufferSizeCheckedAt;
        private int cachedBufferedSeconds;
        internal int BufferedSeconds
        {
            get
            {
                if (activeRing != null && bufferProgress != null)
                    return Math.Clamp(bufferProgress.DurationSeconds, 0, activeRing.Options.ReplaySeconds);
                BufferBytes();
                return cachedBufferedSeconds;
            }
        }
        internal bool RecordingStopping => recordStopTask is { IsCompleted: false };
        public DateTime RecordingSince { get; private set; }

        public Encoder Codec => detected;
        public static Encoder Detected => detected;
        public bool BufferRunning => bufferProc is { HasExited: false };
        public bool Recording => recordProc is { HasExited: false };
        internal bool HasBufferSession => bufferProc != null;
        internal bool HasRecordingSession => recordProc != null;
        internal bool CanSaveReplay => ReplayToSave != null;
        internal bool BufferReceivingFrames => BufferRunning && bufferProgress?.ReceivingFrames(Environment.TickCount64) == true;
        internal bool BufferMatches(IntPtr monitor, Size size) =>
            activeRing?.Options is { } options && options.Monitor == monitor && options.MonitorBounds.Size == size;
        public string? LastError { get; private set; }

        internal sealed class CaptureProgress(long started)
        {
            private long frames;
            private long lastFrame = started;
            private long mediaTimeUs;
            public int DurationSeconds => (int)Math.Max(0, Interlocked.Read(ref mediaTimeUs) / 1_000_000);

            public bool HasFrames => Interlocked.Read(ref frames) > 0;
            public bool ReceivingFrames(long now) => HasFrames && now - Interlocked.Read(ref lastFrame) < 15000;

            public void Observe(string? line, long now)
            {
                if (line != null && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp))
                    Interlocked.Exchange(ref mediaTimeUs, timestamp);
                if (line == null || !line.StartsWith("frame=", StringComparison.Ordinal) ||
                    !long.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) ||
                    count <= frames) return;
                Interlocked.Exchange(ref lastFrame, now);
                Interlocked.Exchange(ref frames, count);
            }

            public bool Stalled(long now, bool interacting)
            {
                // Desktop Duplication repeats static frames. Foreground focus and mouse
                // input have no bearing on whether this capture is healthy.
                return now - Interlocked.Read(ref lastFrame) >= 30000;
            }
        }

        private sealed class ReplayRing(string directory, RecordOptions options)
        {
            public readonly string Directory = directory;
            public readonly RecordOptions Options = options;
            public bool Ready;
            public bool DeletePending;
            public int Readers;
        }

        internal bool BufferStalled(long now, bool interacting) => bufferAudio?.Failed == true || bufferProgress?.Stalled(now, interacting) == true;
        internal bool RecordingStalled(long now, bool interacting) => recordAudio?.Failed == true || recordProgress?.Stalled(now, interacting) == true;

        public static string EncoderName(Encoder e) => e switch
        {
            Encoder.Nvenc => "NVIDIA NVENC",
            Encoder.Amf => "AMD AMF",
            Encoder.Qsv => "Intel Quick Sync",
            Encoder.X264 => "процессор (x264)",
            _ => "—",
        };

        /// <summary>Picks the first hardware encoder that actually works on this PC.</summary>
        public static async Task<Encoder> DetectEncoderAsync()
        {
            if (detected != Encoder.None) return detected;
            foreach (var (enc, name) in new[] { (Encoder.Nvenc, "h264_nvenc"), (Encoder.Amf, "h264_amf"), (Encoder.Qsv, "h264_qsv") })
            {
                if (await FfmpegInstaller.RunAsync($"-f lavfi -i color=c=black:s=640x360:r=30 -frames:v 5 -c:v {name} -f null -") == 0)
                    return detected = enc;
            }
            return detected = Encoder.X264;
        }

        // ---------------- ffmpeg command ----------------

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory(ref Guid iid, out IntPtr factory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumerateDxgi(IntPtr self, uint index, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DescribeOutput(IntPtr self, out OutputDescription description);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputDescription
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int Attached, Rotation;
            public IntPtr Monitor;
        }
        private static T DxgiMethod<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        private static RecordOptions BindCapture(RecordOptions options)
        {
            // Windows DISPLAY numbers are not DXGI output indices. Match HMONITOR,
            // including monitors attached to another graphics adapter.
            Guid iid = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");
            Marshal.ThrowExceptionForHR(CreateDXGIFactory(ref iid, out IntPtr factory));
            try
            {
                var adapters = DxgiMethod<EnumerateDxgi>(factory, 7);
                for (uint a = 0; ; a++)
                {
                    int hr = adapters(factory, a, out IntPtr adapter);
                    if (hr == unchecked((int)0x887A0002)) break;
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        var outputs = DxgiMethod<EnumerateDxgi>(adapter, 7);
                        for (uint o = 0; ; o++)
                        {
                            hr = outputs(adapter, o, out IntPtr output);
                            if (hr == unchecked((int)0x887A0002)) break;
                            Marshal.ThrowExceptionForHR(hr);
                            try
                            {
                                Marshal.ThrowExceptionForHR(DxgiMethod<DescribeOutput>(output, 7)(output, out var desc));
                                if (desc.Attached != 0 && desc.Monitor == options.Monitor)
                                    return options with { CaptureAdapter = (int)a, CaptureOutput = (int)o };
                            }
                            finally { Marshal.Release(output); }
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            throw new IOException("Выбранный монитор сейчас недоступен для записи");
        }

        private string VideoInput(RecordOptions o)
        {
            var sb = new StringBuilder($"ddagrab=output_idx={o.CaptureOutput}:framerate={o.Fps}:draw_mouse=1:dup_frames=1");
            if (o.Width > 0) sb.Append($",scale_d3d11=width={o.Width}:height={o.Height}");
            // GPU frames go straight into NVENC / AMF; Quick Sync and x264 need them in normal memory
            if (Codec is Encoder.Qsv or Encoder.X264) sb.Append(",hwdownload,format=bgra");
            return sb.ToString();
        }

        private string Encode(RecordOptions o)
        {
            int[] cq = { 28, 23, 19 };            // low / medium / high
            int q = cq[Math.Clamp(o.Quality, 0, 2)];
            string keys = $"-g {o.Fps} -force_key_frames expr:gte(t,n_forced*{SegmentSeconds})";
            string v = Codec switch
            {
                Encoder.Nvenc => $"-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq {q} -b:v 0",
                Encoder.Amf => $"-c:v h264_amf -quality balanced -rc cqp -qp_i {q} -qp_p {q + 2}",
                Encoder.Qsv => $"-c:v h264_qsv -preset veryfast -global_quality {q}",
                _ => $"-c:v libx264 -preset veryfast -crf {q + 2} -pix_fmt yuv420p",
            };
            // The default shortest queue can retain TEN SECONDS of uncompressed GPU
            // frames (tens of GB at 4K). Bound it to a small synchronization window.
            string a = o.HasAudio ? "-map 0:a -c:a aac -b:a 160k -shortest -shortest_buf_duration 0.1" : "";
            return $"-map 0:v {v} {keys} -fps_mode vfr {a}";
        }

        private string Inputs(RecordOptions o, RecordingAudio? sessionAudio = null) =>
            $"-init_hw_device d3d11va=capture:{o.CaptureAdapter} -filter_hw_device capture " +
            $"-filter_complex \"{VideoInput(o)},{sessionAudio?.Clock.VideoTimestampFilter ?? "setpts=PTS-STARTPTS"}[v]\" " +
            (o.HasAudio ? "-thread_queue_size 64 -probesize 32 -analyzeduration 0 -f s16le -ar 48000 -ac 2 -i pipe:0 " : "");

        private Process Launch(string args, RecordingAudio? sessionAudio, string logName, CaptureProgress progress)
        {
            var psi = new ProcessStartInfo(FfmpegInstaller.ExePath,
                "-hide_banner -loglevel error -filter_buffered_frames 32 -nostats -stats_period 1 -progress pipe:1 -y " + args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            var p = Process.Start(psi)!;
            try
            {
            job.Add(p); // ffmpeg dies together with SoundShot, even on a crash
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            string log = Path.Combine(SettingsStore.Folder, logName);
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) { LastError = e.Data; try { File.AppendAllText(log, e.Data + "\n"); } catch { } } };
            p.BeginErrorReadLine();
            p.OutputDataReceived += (_, e) => progress.Observe(e.Data, Environment.TickCount64);
            p.BeginOutputReadLine();
            sessionAudio?.AddOutput(p.StandardInput.BaseStream);
            return p;
            }
            catch
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                p.Dispose();
                throw;
            }
        }

        private static RecordingAudio? CreateAudio(RecordOptions options) => options.HasAudio
            ? new RecordingAudio(options.SystemSound, options.Microphone, options.MicrophoneDeviceId) : null;

        // ---------------- replay buffer ----------------

        public void StartBuffer(RecordOptions o, bool preserve = false)
        {
            ObjectDisposedException.ThrowIf(disposalStarted, this);
            o = BindCapture(o);
            StopBuffer(preserve);
            if (!ringInitialized)
            {
                Directory.CreateDirectory(RingDir);
                foreach (var f in Directory.EnumerateFiles(RingDir, "seg*.ts")) { try { File.Delete(f); } catch { } }
                foreach (var d in Directory.EnumerateDirectories(RingDir))
                    if (Guid.TryParseExact(Path.GetFileName(d), "N", out _)) DeleteRingDirectory(d);
                ringInitialized = true;
            }
            string directory = Path.Combine(RingDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            activeRing = new ReplayRing(directory, o);
            try
            {
            bufferAudio = CreateAudio(o);
            // spare segments: saving copies the oldest ones while FFmpeg keeps writing, long replays need more room
            int wrap = o.ReplaySeconds / SegmentSeconds + Math.Max(10, o.ReplaySeconds / 10);
            // Timestamps run on continuously across the segments (no -reset_timestamps): a segment holds 46 or 47
            // AAC frames (0.98 / 1.003 s), so joining zero-based segments shifted the sound by ~20 ms every second -
            // clicks, gaps and drift in saved replays. Continuous MPEG-TS segments are simply glued byte by byte.
            string args = Inputs(o, bufferAudio) + Encode(o).Replace("-map 0:v", "-map [v]") +
                          $" -f segment -segment_time {SegmentSeconds} -segment_format mpegts -segment_wrap {wrap}" +
                          $" -segment_list_size {wrap - 1} -segment_list_type csv -segment_list \"{Path.Combine(directory, "segments.csv")}\"" +
                          $" -flush_packets 1 \"{Path.Combine(directory, "seg%04d.ts")}\"";
            bufferProgress = new CaptureProgress(Environment.TickCount64);
            bufferProc = Launch(args, bufferAudio, $"replay-{logSuffix}.log", bufferProgress);
            }
            catch
            {
                DropAudio(ref bufferAudio);
                ReleaseRing(activeRing); activeRing = null; bufferProgress = null;
                throw;
            }
        }

        public void StopBuffer(bool preserve = false)
        {
            bool withAudio = activeRing?.Options.HasAudio == true;
            StopProcessAsync(DetachProcess(ref bufferProc, bufferAudio), withAudio, preserve ? 250 : 1500).GetAwaiter().GetResult();
            bufferSizeCheckedAt = 0;
            bufferProgress = null;
            if (activeRing is { } ring && preserve && RingReady(ring))
            {
                ReleaseRing(previousRing);
                previousRing = ring;
            }
            else ReleaseRing(activeRing);
            activeRing = null;
            if (!preserve)
            {
                ReleaseRing(previousRing);
                previousRing = null;
            }
            DropAudio(ref bufferAudio);
        }

        private ReplayRing? ReplayToSave => activeRing is { } ring && RingReady(ring) ? ring : previousRing;

        private static List<FileInfo> CompletedSegments(ReplayRing ring)
        {
            var timeline = ReadTimeline(ring);
            if (timeline != null) return timeline.Select(s => s.File).Where(f => f.Exists && f.Length > 0).ToList();
            var segments = new DirectoryInfo(ring.Directory).GetFiles("seg*.ts")
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            // The newest segment can still be open or truncated when a capture is interrupted.
            return segments.Take(Math.Max(0, segments.Count - 1)).Where(f => f.Length > 0).ToList();
        }

        private static List<(FileInfo File, double Start, double End)>? ReadTimeline(ReplayRing ring)
        {
            string path = Path.Combine(ring.Directory, "segments.csv");
            if (!File.Exists(path)) return null;
            var result = new List<(FileInfo, double, double)>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                int last = line.LastIndexOf(','), first = last > 0 ? line.LastIndexOf(',', last - 1) : -1;
                if (first < 0 || !double.TryParse(line.AsSpan(first + 1, last - first - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
                    !double.TryParse(line.AsSpan(last + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double end) || end <= start) continue;
                string name = Path.GetFileName(line[..first].Trim('"'));
                if (!name.StartsWith("seg", StringComparison.Ordinal) || !name.EndsWith(".ts", StringComparison.Ordinal)) continue;
                result.Add((new FileInfo(Path.Combine(ring.Directory, name)), start, end));
            }
            return result;
        }

        private static bool RingReady(ReplayRing ring)
        {
            try { return ring.Ready || (ring.Ready = CompletedSegments(ring).Count > 0); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal void PrunePreviousBuffer()
        {
            if (previousRing == null || activeRing is not { } ring || !RingReady(ring)) return;
            ReleaseRing(previousRing);
            previousRing = null;
            bufferSizeCheckedAt = 0;
        }

        private static void ReleaseRing(ReplayRing? ring)
        {
            if (ring == null) return;
            ring.DeletePending = true;
            if (ring.Readers == 0) DeleteRingDirectory(ring.Directory);
        }

        private static void DeleteRingDirectory(string directory)
        {
            try { Directory.Delete(directory, true); } catch { }
        }

        /// <summary>Saves the last N seconds as an mp4; returns the file path.</summary>
        public async Task<string> SaveReplayAsync(string title)
        {
            var ring = ReplayToSave ?? throw new InvalidOperationException("Буфер ещё пуст — подождите пару секунд");
            var options = ring.Options;
            var complete = CompletedSegments(ring);
            int need = (int)Math.Ceiling(options.ReplaySeconds / (double)SegmentSeconds);
            var take = complete.Skip(Math.Max(0, complete.Count - need)).ToList();
            double trimStart = 0;
            var timeline = ReadTimeline(ring);
            if (timeline is { Count: > 0 })
            {
                double start = Math.Max(timeline[0].Start, timeline[^1].End - options.ReplaySeconds);
                var selected = timeline.Where(s => s.End > start).ToList();
                take = selected.Select(s => s.File).ToList();
                trimStart = Math.Max(0, start - selected[0].Start);
            }
            if (take.Count == 0) throw new InvalidOperationException("Буфер ещё пуст — подождите пару секунд");

            // copy first: FFmpeg keeps overwriting the ring while we join
            string snap = Path.Combine(Path.GetTempPath(), "IOCompanionReplay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snap);
            ring.Readers++;
            string? pending = null;
            try
            {
                // glue the segments into one MPEG-TS stream (oldest first, they are overwritten first). Their
                // timestamps are continuous, so audio stays sample-exact across the joins. Off the UI thread:
                // long replays are gigabytes.
                string joined = Path.Combine(snap, "replay.ts");
                await Task.Run(() =>
                {
                    using var dst = new FileStream(joined, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    foreach (var seg in take)
                    {
                        long start = dst.Position;
                        try
                        {
                            using var src = new FileStream(seg.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                            if (src.Length != seg.Length || File.GetLastWriteTimeUtc(seg.FullName) != seg.LastWriteTimeUtc)
                                throw new IOException("Сегмент буфера уже перезаписан — повторите сохранение");
                            src.CopyTo(dst);
                            if (src.Length != seg.Length || File.GetLastWriteTimeUtc(seg.FullName) != seg.LastWriteTimeUtc)
                                throw new IOException("Сегмент буфера уже перезаписан");
                        }
                        catch (IOException)
                        {
                            // A failed / overwritten segment must not leave a partial TS packet in the snapshot.
                            dst.SetLength(start);
                            dst.Position = start;
                            throw; // Never report a shortened replay as a successful full save.
                        }
                    }
                });
                if (new FileInfo(joined).Length == 0) throw new IOException("Буфер ещё не готов — повторите сохранение через пару секунд");
                Directory.CreateDirectory(options.Folder);
                string target = UniquePath(options.Folder, "Повтор", title, DateTime.Now);
                pending = Path.Combine(Path.GetDirectoryName(target)!, $".replay-{Guid.NewGuid():N}.saving.mp4");
                // A TS declares its AAC stream even when early segments contain no audio packets.
                // The default short probe leaves its sample rate unknown and MP4 fails with a zero-byte file.
                long analyzeUs = (options.ReplaySeconds + 5L) * 1_000_000;
                string log = $"replay-save-{logSuffix}.log";
                int code = await FfmpegInstaller.RunAsync(
                    $"-y -analyzeduration {analyzeUs} -probesize 67108864 -i \"{joined}\" " +
                    $"-ss {trimStart.ToString("0.######", CultureInfo.InvariantCulture)} -map 0:v:0 -map 0:a:0? -t {options.ReplaySeconds} -c copy -abort_on empty_output -avoid_negative_ts make_zero -movflags +faststart \"{pending}\"",
                    Math.Max(120000, options.ReplaySeconds * 1000), log);
                if (code != 0 || !File.Exists(pending) || new FileInfo(pending).Length == 0)
                    throw new IOException($"FFmpeg не смог собрать ролик. Подробности: {Path.Combine(SettingsStore.Folder, log)}");
                return CommitRecording(pending, options.Folder, "Повтор", title, DateTime.Now);
            }
            finally
            {
                if (--ring.Readers == 0 && ring.DeletePending) DeleteRingDirectory(ring.Directory);
                if (pending != null) { try { File.Delete(pending); } catch { } }
                try { Directory.Delete(snap, true); } catch { }
            }
        }

        // ---------------- manual recording ----------------

        public void StartRecording(RecordOptions o, string title)
        {
            ObjectDisposedException.ThrowIf(disposalStarted, this);
            if (Recording || RecordingStopping) return;
            o = BindCapture(o);
            try
            {
            Directory.CreateDirectory(o.Folder);
            recordRoot = o.Folder;
            recordStarted = DateTime.Now;
            recordTemp = Path.ChangeExtension(UniquePath(o.Folder, "Запись", title, recordStarted), $"{Guid.NewGuid():N}.recording.mp4");
            recordAudio = CreateAudio(o);
            string args = Inputs(o, recordAudio) + Encode(o).Replace("-map 0:v", "-map [v]") +
                          $" -movflags +frag_keyframe+empty_moov+default_base_moof \"{recordTemp}\"";
            recordProgress = new CaptureProgress(Environment.TickCount64);
            recordHasAudio = o.HasAudio;
            recordProc = Launch(args, recordAudio, $"record-{logSuffix}.log", recordProgress);
            RecordingSince = DateTime.Now;
            }
            catch
            {
                DropAudio(ref recordAudio);
                recordRoot = recordTemp = null; recordProgress = null; recordHasAudio = false;
                throw;
            }
        }

        /// <summary>Stops and finalises the recording (fragmented → normal mp4); returns the file path.</summary>
        public async Task<string?> StopRecordingAsync(string game, bool interrupted = false)
        {
            if (recordProc == null) return null;
            var process = DetachProcess(ref recordProc, recordAudio);
            string? temp = recordTemp, root = recordRoot;
            DateTime started = recordStarted;
            recordTemp = recordRoot = null;
            recordProgress = null;
            recordStopTask = StopProcessAsync(process, recordHasAudio, interrupted ? 250 : 1500);
            await recordStopTask;
            DropAudio(ref recordAudio);
            if (temp == null || root == null || !File.Exists(temp)) return null;
            if (new FileInfo(temp).Length == 0) { File.Delete(temp); return null; }
            // the folder is chosen now: the game that was really played, not what happened to be in front at the start
            string target = UniquePath(root, "Запись", game, started);
            string pending = Path.Combine(Path.GetDirectoryName(target)!, $".recording-{Guid.NewGuid():N}.saving.mp4");
            try
            {
                int code = await FfmpegInstaller.RunAsync(
                    $"-y -i \"{temp}\" -map 0:v:0 -map 0:a:0? -c copy -abort_on empty_output -movflags +faststart \"{pending}\"",
                    600000, $"record-save-{logSuffix}.log");
                target = UniquePath(root, "Запись", game, started);
                if (code != 0 || !File.Exists(pending) || new FileInfo(pending).Length == 0)
                    throw new IOException($"Не удалось собрать запись. Исходный фрагмент сохранён: {temp}");
                target = CommitRecording(pending, root, "Запись", game, started);
                try { File.Delete(temp); } catch { }
                return target;
            }
            finally { try { File.Delete(pending); } catch { } }
        }

        // ---------------- helpers ----------------

        private string CommitRecording(string pending, string root, string kind, string game, DateTime when)
        {
            // Independent save tasks may finish in the same second. File.Move is the
            // atomic reservation; never overwrite an existing clip or lose the second save.
            while (true)
            {
                string target = UniquePath(root, kind, game, when);
                try { File.Move(pending, target); return target; }
                catch (IOException) when (File.Exists(target)) { }
            }
        }

        /// <summary>Layout: <root>/Монитор N/<программа>/<дата> — <тип>.<расширение>.</summary>
        private string UniquePath(string root, string kind, string game, DateTime when, string extension = ".mp4")
        {
            string folder = Path.Combine(root, SafeName(MonitorName), SafeName(game));
            Directory.CreateDirectory(folder);
            string file = $"{when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)} — {kind}";
            string path = Path.Combine(folder, file + extension);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{file} ({i}){extension}");
            return path;
        }

        internal static async Task<int> OrganizeLegacyFoldersAsync(string root)
        {
            await folderOrganizationGate.WaitAsync();
            try { return await Task.Run(() => OrganizeLegacyFolders(root)); }
            finally { folderOrganizationGate.Release(); }
        }

        private static int OrganizeLegacyFolders(string root)
        {
            var parent = new DirectoryInfo(Path.GetFullPath(root));
            if (!parent.Exists) return 0;
            int moved = 0;
            foreach (var monitor in parent.EnumerateDirectories("Монитор *"))
            {
                if ((monitor.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !int.TryParse(monitor.Name.AsSpan("Монитор ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
                    number <= 0) continue;
                foreach (string kind in new[] { "Видео", "Фото" })
                {
                    var legacy = new DirectoryInfo(Path.Combine(monitor.FullName, kind));
                    if (!legacy.Exists || (legacy.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (var game in legacy.EnumerateDirectories())
                    {
                        if ((game.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        string destination = Path.Combine(monitor.FullName, game.Name);
                        Directory.CreateDirectory(destination);
                        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) continue;
                        foreach (var file in game.EnumerateFiles())
                        {
                            string extension = kind == "Видео" ? ".mp4" : ".png";
                            if (!file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase) ||
                                file.Name.StartsWith(".", StringComparison.Ordinal) ||
                                file.Name.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase) ||
                                file.Name.EndsWith(".saving.mp4", StringComparison.OrdinalIgnoreCase) ||
                                (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            string target = Path.Combine(destination, file.Name);
                            for (int suffix = 2; ; suffix++)
                            {
                                try
                                {
                                    File.Move(file.FullName, target);
                                    moved++;
                                    break;
                                }
                                catch (IOException) when (File.Exists(target))
                                {
                                    target = Path.Combine(destination, $"{Path.GetFileNameWithoutExtension(file.Name)} ({suffix}){file.Extension}");
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    SettingsStore.LogError(ex);
                                    break;
                                }
                            }
                        }
                        try { game.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                    try { legacy.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
            return moved;
        }

        public static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
            return s.Length == 0 ? "Запись экрана" : s.Length > 60 ? s[..60].Trim() : s;
        }

        /// <summary>Screenshot of one monitor as PNG in the game's folder (FFmpeg sees games that GDI shows black).</summary>
        public async Task<string> ScreenshotAsync(RecordOptions o, string game, System.Drawing.Rectangle bounds)
        {
            DateTime when = DateTime.Now;
            string target = UniquePath(o.Folder, "Скриншот", game, when, ".png");
            string pending = Path.Combine(Path.GetDirectoryName(target)!, ".shot-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                bool captured = false;
                if (FfmpegInstaller.Installed)
                {
                    int code = await FfmpegInstaller.RunAsync(
                        $"-y -filter_complex \"gfxcapture=hmonitor={(ulong)o.Monitor}:max_framerate=5:capture_cursor=0,hwdownload,format=bgra[v]\" -map [v] -frames:v 1 \"{pending}\"", 2500);
                    captured = code == 0 && File.Exists(pending) && new FileInfo(pending).Length > 0;
                }
                if (!captured) await Task.Run(() =>
                {
                    using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                    using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
                    bmp.Save(pending, System.Drawing.Imaging.ImageFormat.Png);
                });
                while (true)
                {
                    target = UniquePath(o.Folder, "Скриншот", game, when, ".png");
                    try { File.Move(pending, target); return target; }
                    catch (IOException) when (File.Exists(target)) { }
                }
            }
            finally { try { File.Delete(pending); } catch (IOException) { } }
        }

        private Process? DetachProcess(ref Process? p, RecordingAudio? sessionAudio)
        {
            var proc = p;
            p = null;
            if (proc == null) return null;
            try { sessionAudio?.RemoveOutput(proc.StandardInput.BaseStream); } catch { }
            return proc;
        }

        private static async Task StopProcessAsync(Process? proc, bool withAudio, int graceMs)
        {
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    // PCM owns stdin when audio is enabled. EOF plus -shortest finishes both
                    // streams. With no audio stdin accepts FFmpeg's normal quit command.
                    // Closing a pipe can wait for a blocked PCM write. Keep that wait
                    // off the dispatcher and include it in the process stop deadline.
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            if (!withAudio) proc.StandardInput.WriteLine("q");
                            proc.StandardInput.Close();
                        }
                        catch { }
                    });
                    var exited = proc.WaitForExitAsync();
                    if (await Task.WhenAny(exited, Task.Delay(graceMs)).ConfigureAwait(false) != exited)
                        proc.Kill(true);
                    await exited.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        private static void DropAudio(ref RecordingAudio? audio)
        {
            var previous = audio;
            audio = null;
            try { previous?.Dispose(); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        /// <summary>Disk space the ring currently uses.</summary>
        public long BufferBytes()
        {
            long now = Environment.TickCount64;
            if (bufferSizeCheckedAt != 0 && now - bufferSizeCheckedAt < 3000) return cachedBufferBytes;
            try
            {
                cachedBufferBytes = (activeRing == null ? 0 : new DirectoryInfo(activeRing.Directory).EnumerateFiles().Sum(f => f.Length)) +
                                    (previousRing == null ? 0 : new DirectoryInfo(previousRing.Directory).EnumerateFiles().Sum(f => f.Length));
                var savedRing = ReplayToSave;
                cachedBufferedSeconds = savedRing == null ? 0 : Math.Min(savedRing.Options.ReplaySeconds,
                    CompletedSegments(savedRing).Count * SegmentSeconds);
                if (savedRing != null && ReadTimeline(savedRing) is { Count: > 0 } timeline)
                    cachedBufferedSeconds = Math.Min(savedRing.Options.ReplaySeconds, (int)(timeline[^1].End - timeline[0].Start));
                bufferSizeCheckedAt = now;
                return cachedBufferBytes;
            }
            catch { return 0; }
        }

        public void Dispose()
        {
            if (disposalStarted) return;
            disposalStarted = true;
            StopBuffer();
            StopProcessAsync(DetachProcess(ref recordProc, recordAudio), recordHasAudio, 1500).GetAwaiter().GetResult();
            DropAudio(ref bufferAudio);
            DropAudio(ref recordAudio);
            job.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (disposalStarted) return;
            disposalStarted = true;
            bool bufferWithAudio = activeRing?.Options.HasAudio == true;
            var buffer = DetachProcess(ref bufferProc, bufferAudio);
            var recording = DetachProcess(ref recordProc, recordAudio);
            await Task.WhenAll(StopProcessAsync(buffer, bufferWithAudio, 250),
                StopProcessAsync(recording, recordHasAudio, 1500), recordStopTask ?? Task.CompletedTask);
            bufferProgress = recordProgress = null;
            ReleaseRing(activeRing);
            ReleaseRing(previousRing);
            activeRing = previousRing = null;
            DropAudio(ref bufferAudio);
            DropAudio(ref recordAudio);
            job.Dispose();
        }
    }

    /// <summary>Windows job object: child processes are killed when SoundShot exits for any reason.</summary>
    internal sealed class JobObject : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref Extended info, int size);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct Basic { public long a, b; public uint LimitFlags; public UIntPtr c, d; public uint e; public UIntPtr f; public uint g, h; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Io { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Extended { public Basic Basic; public Io Io; public UIntPtr p1, p2, p3, p4; }

        private IntPtr handle;

        public JobObject()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            var info = new Extended { Basic = new Basic { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            SetInformationJobObject(handle, 9, ref info, Marshal.SizeOf<Extended>());
        }

        public void Add(Process p) { try { AssignProcessToJobObject(handle, p.Handle); } catch { } }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
            handle = IntPtr.Zero;
        }
    }
}
