using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// FFmpeg (GPL build by Gyan Doshi, the one linked from ffmpeg.org) is downloaded once, on first use of
    /// screen recording, from the pinned official GitHub release and checked by SHA-256. Only ffmpeg.exe is kept.
    /// </summary>
    public static class FfmpegInstaller
    {
        public const string Version = "9.0.2";
        private const string Url = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
        private const string Sha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";

        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GraphiteCompanion", "ffmpeg-" + Version);

        public static string ExePath => Path.Combine(Folder, "ffmpeg.exe");
        public static bool Installed => File.Exists(ExePath);

        public static async Task InstallAsync(IProgress<double>? progress = null)
        {
            Directory.CreateDirectory(Folder);
            string zip = Path.Combine(Folder, "download.zip");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SoundShot");
                using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1, done = 0;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zip);
                var buf = new byte[1 << 16];
                int read;
                while ((read = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            try
            {
                string hash;
                await using (var s = File.OpenRead(zip)) hash = Convert.ToHexString(await SHA256.HashDataAsync(s)).ToLowerInvariant();
                if (hash != Sha256) throw new InvalidOperationException("Скачанный FFmpeg не совпал с официальным (контрольная сумма). Установка отменена.");
                using var archive = ZipFile.OpenRead(zip);
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("В архиве нет ffmpeg.exe");
                entry.ExtractToFile(ExePath + ".part", true);
                File.Move(ExePath + ".part", ExePath, true);
            }
            finally
            {
                try { File.Delete(zip); } catch { }
            }
        }

        /// <summary>Runs ffmpeg with arguments and waits; returns the exit code (used for quick capability tests).</summary>
        public static async Task<int> RunAsync(string args, int timeoutMs = 15000, string? logName = null)
        {
            var psi = new ProcessStartInfo(ExePath, "-hide_banner -loglevel error " + args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi)!;
            var errors = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEndAsync();
            var exited = p.WaitForExitAsync();
            bool timedOut = false;
            if (await Task.WhenAny(exited, Task.Delay(timeoutMs)) != exited)
            {
                timedOut = true;
                try { p.Kill(true); } catch { }
            }
            await exited;
            string stderr = await errors;
            await output;
            int code = timedOut ? -1 : p.ExitCode;
            if (logName != null && (code != 0 || stderr.Length > 0))
            {
                try
                {
                    Directory.CreateDirectory(SettingsStore.Folder);
                    File.AppendAllText(Path.Combine(SettingsStore.Folder, logName),
                        $"[{DateTime.Now:O}] Exit code: {code}{(timedOut ? " (timeout)" : "")}\n{stderr}\n");
                }
                catch { }
            }
            return code;
        }
    }
}
