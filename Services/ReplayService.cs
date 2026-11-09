using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GraphiteCompanion.Audio;

namespace GraphiteCompanion.Services
{
    public sealed class RecordOptions
    {
        public IntPtr Monitor;            // HMONITOR to capture
        public int Width, Height;         // 0 = native
        public int Fps = 60;
        public int Quality = 1;           // 0 low, 1 medium, 2 high
        public bool SystemSound = true;
        public bool Microphone;
        public int ReplaySeconds = 60;
        public string Folder = "";
    }

    /// <summary>
    /// Screen recording through FFmpeg with the GPU's own encoder (NVENC / AMF / Quick Sync, x264 as last resort).
    /// Replay buffer: FFmpeg keeps writing short segments into a ring in %LOCALAPPDATA%; saving copies the last
    /// N seconds and joins them into one mp4 without re-encoding. Manual recording: a second FFmpeg writes a
    /// fragmented mp4 straight into the target folder (safe even if the PC crashes), finalised on stop.
    /// </summary>
    public sealed class ReplayService : IDisposable
    {
        public enum Encoder { None, Nvenc, Amf, Qsv, X264 }

        private const int SegmentSeconds = 1;
        private readonly string RingDir;
        private readonly string logSuffix;
        private static Encoder detected;

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
        private RecordingAudio? audio;
        private Process? bufferProc, recordProc;
        private RecordOptions? opts;
        private string? recordTemp, recordRoot;
        private DateTime recordStarted;
        public DateTime RecordingSince { get; private set; }

        public Encoder Codec => detected;
        public static Encoder Detected => detected;
        public bool BufferRunning => bufferProc is { HasExited: false };
        public bool Recording => recordProc is { HasExited: false };
        public string? LastError { get; private set; }

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

        private string VideoInput(RecordOptions o)
        {
            var sb = new StringBuilder($"gfxcapture=hmonitor={(ulong)o.Monitor}:max_framerate={o.Fps}:capture_cursor=1");
            if (o.Width > 0) sb.Append($":width={o.Width}:height={o.Height}:resize_mode=scale_aspect");
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
            string a = o.SystemSound || o.Microphone ? "-map 0:a -c:a aac -b:a 160k" : "";
            return $"-map 0:v {v} {keys} {a}";
        }

        private string Inputs(RecordOptions o) =>
            $"-filter_complex \"{VideoInput(o)},setpts=PTS-STARTPTS[v]\" " +
            (o.SystemSound || o.Microphone ? "-f s16le -ar 48000 -ac 2 -i pipe:0 " : "");

        private Process Launch(string args, bool withAudio, string logName)
        {
            var psi = new ProcessStartInfo(FfmpegInstaller.ExePath, "-hide_banner -loglevel error -y " + args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            var p = Process.Start(psi)!;
            job.Add(p); // ffmpeg dies together with SoundShot, even on a crash
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            string log = Path.Combine(SettingsStore.Folder, logName);
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) { LastError = e.Data; try { File.AppendAllText(log, e.Data + "\n"); } catch { } } };
            p.BeginErrorReadLine();
            p.OutputDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            if (withAudio) audio?.AddOutput(p.StandardInput.BaseStream);
            return p;
        }

        private void EnsureAudio(RecordOptions o)
        {
            if (audio == null && (o.SystemSound || o.Microphone)) audio = new RecordingAudio(o.SystemSound, o.Microphone);
        }

        // ---------------- replay buffer ----------------

        public void StartBuffer(RecordOptions o)
        {
            StopBuffer();
            opts = o;
            Directory.CreateDirectory(RingDir);
            foreach (var f in Directory.GetFiles(RingDir)) { try { File.Delete(f); } catch { } }
            EnsureAudio(o);
            // spare segments: saving copies the oldest ones while FFmpeg keeps writing, long replays need more room
            int wrap = o.ReplaySeconds / SegmentSeconds + Math.Max(10, o.ReplaySeconds / 10);
            string args = Inputs(o) + Encode(o).Replace("-map 0:v", "-map [v]") +
                          $" -f segment -segment_time {SegmentSeconds} -segment_format mpegts -segment_wrap {wrap}" +
                          $" -reset_timestamps 1 -flush_packets 1 \"{Path.Combine(RingDir, "seg%04d.ts")}\"";
            bufferProc = Launch(args, o.SystemSound || o.Microphone, $"replay-{logSuffix}.log");
        }

        public void StopBuffer()
        {
            Kill(ref bufferProc);
            if (!Recording) DropAudio();
        }

        /// <summary>Saves the last N seconds as an mp4; returns the file path.</summary>
        public async Task<string> SaveReplayAsync(string title)
        {
            if (opts == null || !BufferRunning) throw new InvalidOperationException("Повторы не запущены");
            var segs = new DirectoryInfo(RingDir).GetFiles("seg*.ts")
                .Where(f => f.Length > 0).OrderBy(f => f.LastWriteTimeUtc).ToList();
            int need = (int)Math.Ceiling(opts.ReplaySeconds / (double)SegmentSeconds) + 1;
            var take = segs.Skip(Math.Max(0, segs.Count - need)).ToList();
            if (take.Count == 0) throw new InvalidOperationException("Буфер ещё пуст — подождите пару секунд");

            // copy first: FFmpeg keeps overwriting the ring while we join
            string snap = Path.Combine(Path.GetTempPath(), "IOCompanionReplay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(snap);
            try
            {
                var list = new StringBuilder();
                // oldest first (they are overwritten first); off the UI thread - long replays are gigabytes
                await Task.Run(() =>
                {
                    for (int i = 0; i < take.Count; i++)
                    {
                        string dst = Path.Combine(snap, $"p{i:0000}.ts");
                        try { File.Copy(take[i].FullName, dst, true); }
                        catch (IOException) { continue; } // segment being rewritten right now: skip it
                        list.Append("file '").Append(dst.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
                    }
                });
                string listFile = Path.Combine(snap, "list.txt");
                await File.WriteAllTextAsync(listFile, list.ToString());
                Directory.CreateDirectory(opts.Folder);
                string target = UniquePath(opts.Folder, VideoDir, title, DateTime.Now);
                int code = await FfmpegInstaller.RunAsync($"-y -f concat -safe 0 -i \"{listFile}\" -c copy -movflags +faststart \"{target}\"", Math.Max(120000, opts.ReplaySeconds * 1000));
                if (code != 0 || !File.Exists(target)) throw new IOException("FFmpeg не смог собрать ролик");
                return target;
            }
            finally
            {
                try { Directory.Delete(snap, true); } catch { }
            }
        }

        // ---------------- manual recording ----------------

        public void StartRecording(RecordOptions o, string title)
        {
            if (Recording) return;
            opts ??= o;
            EnsureAudio(o);
            Directory.CreateDirectory(o.Folder);
            recordRoot = o.Folder;
            recordStarted = DateTime.Now;
            recordTemp = Path.ChangeExtension(UniquePath(o.Folder, VideoDir, title, recordStarted), ".recording.mp4");
            string args = Inputs(o) + Encode(o).Replace("-map 0:v", "-map [v]") +
                          $" -movflags +frag_keyframe+empty_moov+default_base_moof \"{recordTemp}\"";
            recordProc = Launch(args, o.SystemSound || o.Microphone, $"record-{logSuffix}.log");
            RecordingSince = DateTime.Now;
        }

        /// <summary>Stops and finalises the recording (fragmented → normal mp4); returns the file path.</summary>
        public async Task<string?> StopRecordingAsync(string game)
        {
            if (recordProc == null) return null;
            Kill(ref recordProc);
            if (!BufferRunning) DropAudio();
            string? temp = recordTemp, root = recordRoot;
            recordTemp = recordRoot = null;
            if (temp == null || root == null || !File.Exists(temp)) return null;
            // the folder is chosen now: the game that was really played, not what happened to be in front at the start
            string target = UniquePath(root, VideoDir, game, recordStarted);
            await Task.Delay(300);
            int code = await FfmpegInstaller.RunAsync($"-y -i \"{temp}\" -c copy -movflags +faststart \"{target}\"", 600000);
            if (code == 0 && File.Exists(target)) { try { File.Delete(temp); } catch { } return target; }
            // keep the fragmented file - it still plays everywhere
            string fallback = Path.ChangeExtension(target, ".mp4");
            try { File.Move(temp, fallback, true); } catch { fallback = temp; }
            return fallback;
        }

        // ---------------- helpers ----------------

        public const string VideoDir = "Видео", ShotDir = "Фото";

        /// <summary>Layout: <root>/Монитор N/Видео/<программа>/… and <root>/Монитор N/Фото/<программа>/…</summary>
        private string UniquePath(string root, string kind, string game, DateTime when)
        {
            string name = SafeName(game);
            string folder = Path.Combine(root, SafeName(MonitorName), kind, name);
            Directory.CreateDirectory(folder);
            string file = $"{name} {when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}";
            string path = Path.Combine(folder, file + ".mp4");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{file} ({i}).mp4");
            return path;
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
            string path = UniquePath(o.Folder, ShotDir, game, DateTime.Now);
            path = Path.ChangeExtension(path, ".png");
            if (FfmpegInstaller.Installed)
            {
                int code = await FfmpegInstaller.RunAsync(
                    $"-y -filter_complex \"gfxcapture=hmonitor={(ulong)o.Monitor}:max_framerate=5:capture_cursor=0,hwdownload,format=bgra[v]\" -map [v] -frames:v 1 \"{path}\"", 10000);
                if (code == 0 && File.Exists(path) && new FileInfo(path).Length > 0) return path;
            }
            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        private void Kill(ref Process? p)
        {
            var proc = p;
            p = null;
            if (proc == null) return;
            try { audio?.RemoveOutput(proc.StandardInput.BaseStream); } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    // ask nicely first so the last fragment is written, then force
                    try { proc.StandardInput.Close(); } catch { }
                    if (!proc.WaitForExit(1500)) proc.Kill(true);
                }
            }
            catch { }
            proc.Dispose();
        }

        private void DropAudio()
        {
            audio?.Dispose();
            audio = null;
        }

        /// <summary>Disk space the ring currently uses.</summary>
        public long BufferBytes()
        {
            try { return Directory.Exists(RingDir) ? new DirectoryInfo(RingDir).GetFiles().Sum(f => f.Length) : 0; }
            catch { return 0; }
        }

        public void Dispose()
        {
            StopBuffer();
            Kill(ref recordProc);
            DropAudio();
            job.Dispose();
            try { if (Directory.Exists(RingDir)) Directory.Delete(RingDir, true); } catch { }
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
