using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GraphiteCompanion.Services
{
    public sealed record UpdateInfo(Version Version, string Notes, string DownloadUrl, string AssetName);

    /// <summary>
    /// Checks the latest GitHub release and installs it: downloads the asset, then a small
    /// PowerShell script waits for the app to exit, replaces the exe and starts it again.
    /// </summary>
    public static class UpdateService
    {
        /// <summary>GitHub repository "owner/name" with the releases. Can be overridden by update-repo.txt next to the exe.</summary>
        public const string DefaultRepo = "RomanGeniev/soundshot";

        public static string Repo
        {
            get
            {
                try
                {
                    string file = Path.Combine(AppContext.BaseDirectory, "update-repo.txt");
                    if (File.Exists(file)) return File.ReadAllText(file).Trim();
                }
                catch { }
                return DefaultRepo;
            }
        }

        public static bool Configured => Repo.Contains('/');

        public static Version Current =>
            Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);

        public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

        private static HttpClient Client()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SoundShot-Updater");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        /// <summary>Returns the newer release, or null when up to date / not configured / offline.</summary>
        public static async Task<UpdateInfo?> CheckAsync()
        {
            if (!Configured) return null;
            using var http = Client();
            string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (version <= Current) return null;

            var asset = root.GetProperty("assets").EnumerateArray()
                .Select(a => (name: a.GetProperty("name").GetString() ?? "", url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .Where(a => a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || a.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (asset.url == null || asset.url.Length == 0) return null;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(version, notes, asset.url, asset.name);
        }

        internal static string FindUpdateExecutable(string directory) =>
            Directory.GetFiles(directory, "SoundShot.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? Directory.GetFiles(directory, "GraphiteCompanion.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("В архиве обновления нет SoundShot.exe");

        /// <summary>Downloads the update and launches the replace-and-restart script. The caller must exit the app afterwards.</summary>
        public static async Task InstallAsync(UpdateInfo info, IProgress<double>? progress = null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "IOCompanionUpdate");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string download = Path.Combine(dir, info.AssetName);

            using (var http = Client())
            using (var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(download);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }

            string newExe = download;
            if (download.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string extract = Path.Combine(dir, "x");
                ZipFile.ExtractToDirectory(download, extract);
                newExe = FindUpdateExecutable(extract);
            }

            string target = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь программы");
            string script = Path.Combine(dir, "update.ps1");
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            File.WriteAllText(script,
                $"Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue\n" +
                "Start-Sleep -Milliseconds 300\n" +
                $"Copy-Item -LiteralPath {Q(newExe)} -Destination {Q(target)} -Force\n" +
                $"Start-Process -FilePath {Q(target)}\n",
                new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
    }
}
