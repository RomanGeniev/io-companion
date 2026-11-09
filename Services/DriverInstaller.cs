using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GraphiteCompanion.Services
{
    public sealed record DriverModel(string Title, string FolderPrefix, string FilePattern);

    /// <summary>
    /// Installs the official IO driver package (it contains the C-Media driver + effects module the
    /// equalizer needs). The installer is always taken from the manufacturer's public folder - never
    /// redistributed by us. Yandex Disk sometimes refuses automated downloads (HTTP 429); then the folder
    /// is opened in the browser and the downloaded file is picked up from the Downloads folder.
    /// </summary>
    public static class DriverInstaller
    {
        public const string PublicKey = "https://disk.yandex.ru/d/3s8_z8ttXK438w";
        public const string HeadsetFolderUrl = "https://disk.yandex.ru/d/3s8_z8ttXK438w/Наушники";
        private const string Root = "/Наушники";

        public static readonly DriverModel[] Models =
        {
            new("Graphite 2 Pro / Pro Max (беспроводные)", "Graphite 2 Pro", "*graphite_2_pro*audio_center*.exe"),
            new("Graphite 2 Wired (проводные)", "Graphite 2 Wired", "*graphite2*audio_center*.exe"),
        };

        public static DriverModel Guess(string deviceName) =>
            deviceName.Contains("Pro", StringComparison.OrdinalIgnoreCase) ? Models[0] : Models[1];

        private static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SoundShot");
            return c;
        }

        private static string Api(string what, string path) =>
            $"https://cloud-api.yandex.net/v1/disk/public/resources{what}?public_key={Uri.EscapeDataString(PublicKey)}&path={Uri.EscapeDataString(path)}&limit=100";

        /// <summary>Downloads the installer for the model; returns null when Yandex refuses (rate limit) or it is offline.</summary>
        public static async Task<string?> TryDownloadAsync(DriverModel model, IProgress<double>? progress = null)
        {
            try
            {
                using var http = Client();
                using var root = JsonDocument.Parse(await http.GetStringAsync(Api("", Root)));
                string? folder = root.RootElement.GetProperty("_embedded").GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("name").GetString())
                    .FirstOrDefault(n => n != null && n.StartsWith(model.FolderPrefix, StringComparison.OrdinalIgnoreCase));
                if (folder == null) return null;

                using var dir = JsonDocument.Parse(await http.GetStringAsync(Api("", $"{Root}/{folder}")));
                string? file = dir.RootElement.GetProperty("_embedded").GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("name").GetString())
                    .FirstOrDefault(n => n != null && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (file == null) return null;

                using var link = JsonDocument.Parse(await http.GetStringAsync(Api("/download", $"{Root}/{folder}/{file}")));
                string href = link.RootElement.GetProperty("href").GetString() ?? "";
                if (href.Length == 0) return null;

                string target = Path.Combine(Path.GetTempPath(), "IOCompanionDriver", file);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var response = await http.GetAsync(href, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) return null;
                long total = response.Content.Headers.ContentLength ?? -1, done = 0;
                await using (var src = await response.Content.ReadAsStreamAsync())
                await using (var dst = File.Create(target))
                {
                    var buf = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read));
                        done += read;
                        if (total > 0) progress?.Report((double)done / total);
                    }
                }
                return target;
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                return null;
            }
        }

        public static string DownloadsFolder
        {
            get
            {
                try
                {
                    // "Downloads" known folder (can be moved to another drive)
                    using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                    if (k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p && Directory.Exists(p)) return p;
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        /// <summary>Newest fully downloaded installer for the model in the Downloads folder, or null.</summary>
        public static string? FindDownloaded(DriverModel model, DateTime? notOlderThan = null)
        {
            try
            {
                return new DirectoryInfo(DownloadsFolder).GetFiles(model.FilePattern)
                    .Where(f => f.Length > 1_000_000 && (notOlderThan == null || f.LastWriteTime >= notOlderThan))
                    .OrderByDescending(f => f.LastWriteTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        public static void OpenOfficialFolder() =>
            Process.Start(new ProcessStartInfo(HeadsetFolderUrl) { UseShellExecute = true });

        /// <summary>True when the official Audio Center starts with Windows (machine-wide Run key).</summary>
        public static bool AudioCenterAutostarts
        {
            get
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                    return k != null && k.GetValueNames().Any(n => IsAudioCenter(k.GetValue(n) as string));
                }
                catch { return false; }
            }
        }

        private static bool IsAudioCenter(string? cmd) =>
            cmd != null && (cmd.Contains("audio_center", StringComparison.OrdinalIgnoreCase) ||
                            cmd.Contains("Audio Center", StringComparison.OrdinalIgnoreCase));

        private const string RemoveAutostartScript =
            "$run='HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run'\n" +
            "(Get-ItemProperty $run).PSObject.Properties | Where-Object { \"$($_.Value)\" -match '(?i)audio_center|Audio Center' } |\n" +
            "  ForEach-Object { Remove-ItemProperty -Path $run -Name $_.Name -ErrorAction SilentlyContinue }\n" +
            "Get-Process | Where-Object { $_.Path -match '(?i)audio_center' } | Stop-Process -Force -ErrorAction SilentlyContinue\n";

        /// <summary>Removes Audio Center from autostart (asks for administrator rights). False if the user declined.</summary>
        public static Task<bool> RemoveAudioCenterAutostartAsync() => RunElevatedAsync(RemoveAutostartScript);

        /// <summary>Runs the official installer silently (one UAC prompt), then removes Audio Center from autostart.</summary>
        public static Task<bool> InstallAsync(string setupPath)
        {
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            string script =
                $"$p = Start-Process -FilePath {Q(setupPath)} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -Wait -PassThru\n" +
                "Start-Sleep -Seconds 2\n" + RemoveAutostartScript + "exit $p.ExitCode\n";
            return RunElevatedAsync(script);
        }

        private static async Task<bool> RunElevatedAsync(string script)
        {
            string file = Path.Combine(Path.GetTempPath(), $"iocompanion_{Guid.NewGuid():N}.ps1");
            await File.WriteAllTextAsync(file, script, new UTF8Encoding(true));
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{file}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (p == null) return false;
                await p.WaitForExitAsync();
                return p.ExitCode == 0;
            }
            catch (Win32Exception)
            {
                return false; // the user pressed "No" in the UAC prompt
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}
