using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace GraphiteCompanion.Services
{
    public sealed record GalleryItem(string Path, string Name, string Folder, bool Video, DateTime Modified, long Bytes)
    {
        public string Monitor { get; init; } = "Без монитора";
        public string Application { get; init; } = "Без приложения";
    }
    public sealed record GalleryScan(IReadOnlyList<GalleryItem> Items, bool Limited, bool Missing);

    public static class GalleryService
    {
        public static bool IsMediaFile(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            if (name.StartsWith('.') || name.EndsWith(".recording.mp4", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".saving.mp4", StringComparison.OrdinalIgnoreCase)) return false;
            return System.IO.Path.GetExtension(name).ToLowerInvariant() is ".mp4" or ".png" or ".jpg" or ".jpeg";
        }

        public static GalleryScan Scan(string root, CancellationToken token)
        {
            root = System.IO.Path.GetFullPath(root);
            if (!Directory.Exists(root)) return new(Array.Empty<GalleryItem>(), false, true);
            var latest = new PriorityQueue<GalleryItem, long>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System, MaxRecursionDepth = 16 };
            int found = 0;
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                token.ThrowIfCancellationRequested();
                if (!IsMediaFile(path)) continue;
                try
                {
                    var file = new FileInfo(path);
                    if (file.Length == 0) continue;
                    string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, path)) ?? "";
                    var item = new GalleryItem(path, System.IO.Path.GetFileNameWithoutExtension(path), folder.Replace('\\', '·').Replace("·", " · "), file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase), file.LastWriteTimeUtc, file.Length);
                    var parts = folder.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    bool hasMonitor = parts.Length > 0 && (System.Text.RegularExpressions.Regex.IsMatch(parts[0], @"^Монитор [1-9]\d*$") || parts[0] == "Несколько мониторов");
                    item = item with { Monitor = hasMonitor ? parts[0] : "Без монитора", Application = parts.Length > (hasMonitor ? 1 : 0) ? parts[hasMonitor ? 1 : 0] : "Без приложения" };
                    latest.Enqueue(item, item.Modified.Ticks); found++;
                    if (latest.Count > 500) latest.Dequeue();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return new(latest.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Modified).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray(), found > 500, false);
        }

        public static async Task<BitmapSource?> ThumbnailAsync(GalleryItem item, CancellationToken token, double positionSeconds = 0, int width = 320)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!item.Video) return await Task.Run(() =>
                {
                    using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return Decode(stream);
                }, token);
                if (!FfmpegInstaller.Installed) return null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var start = new ProcessStartInfo(FfmpegInstaller.ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                width = Math.Clamp(width, 320, 1920);
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_threads", "1", "-threads", "1", "-protocol_whitelist", "file,pipe", "-ss", Math.Max(0, positionSeconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", item.Path, "-map", "0:v:0", "-frames:v", "1", "-an", "-vf", $"scale={width}:{width * 9 / 16}:force_original_aspect_ratio=decrease", "-threads", "1", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" }) start.ArgumentList.Add(arg);
                using var process = new Process { StartInfo = start };
                process.Start();
                try
                {
                    var error = process.StandardError.ReadToEndAsync(timeout.Token);
                    using var output = new MemoryStream();
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        if (output.Length + count > 2_000_000) return null;
                        output.Write(buffer, 0, count);
                    }
                    await process.WaitForExitAsync(timeout.Token); await error;
                    if (process.ExitCode != 0 || output.Length == 0) return null;
                    output.Position = 0; return Decode(output, width);
                }
                finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        }

        private static BitmapSource Decode(Stream stream, int width = 320)
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }

        public static string SaveRegionPng(BitmapSource image, string folder, DateTime timestamp)
        {
            Directory.CreateDirectory(folder);
            string pending = System.IO.Path.Combine(folder, ".region-" + Guid.NewGuid().ToString("N") + ".saving");
            try
            {
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(stream);
                }
                string name = timestamp.ToString("yyyy-MM-dd HH-mm-ss") + " — Снимок области";
                for (int number = 1; ; number++)
                {
                    string target = System.IO.Path.Combine(folder, name + (number == 1 ? "" : $" ({number})") + ".png");
                    try { File.Move(pending, target); return target; }
                    catch (IOException) when (File.Exists(target)) { }
                }
            }
            finally { try { if (File.Exists(pending)) File.Delete(pending); } catch (IOException) { } }
        }
    }
}
