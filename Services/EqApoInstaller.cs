using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using GraphiteCompanion.Audio;

namespace GraphiteCompanion.Services
{
    /// <summary>
    /// Installs Equalizer APO (GPL, https://sourceforge.net/projects/equalizerapo/) from its official
    /// SourceForge release. The download is verified by SHA-256 before anything is run.
    /// During setup Equalizer APO's own Device Selector opens - the user ticks their headphones there.
    /// Afterwards the "Users" group gets write access to the config folder so SoundShot can control it.
    /// </summary>
    public static class EqApoInstaller
    {
        public const string Version = "1.4.2";
        private const string Url = "https://sourceforge.net/projects/equalizerapo/files/1.4.2/EqualizerAPO-x64-1.4.2.exe/download";
        private const string Sha256 = "7403be7427bbe1936a40dded082829b6e217fc4f5990fee5cba501f0ae055afa";

        /// <summary>Downloads and verifies the installer. Throws with a readable message on failure.</summary>
        public static async Task<string> DownloadAsync(IProgress<double>? progress = null)
        {
            string target = Path.Combine(Path.GetTempPath(), "IOCompanionEqApo", $"EqualizerAPO-x64-{Version}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && HashOf(target) == Sha256) return target;

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SoundShot");
            using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
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
            if (HashOf(target) != Sha256)
            {
                File.Delete(target);
                throw new InvalidOperationException("Скачанный файл не совпал с официальным (контрольная сумма). Установка отменена.");
            }
            return target;
        }

        private static string HashOf(string file)
        {
            using var s = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }

        private const string GrantConfigAccess =
            "$cfg = (Get-ItemProperty 'HKLM:\\SOFTWARE\\EqualizerAPO' -ErrorAction SilentlyContinue).ConfigPath\n" +
            "if (-not $cfg) { $cfg = Join-Path $env:ProgramFiles 'EqualizerAPO\\config' }\n" +
            "if (Test-Path $cfg) { icacls \"$cfg\" /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null }\n";

        /// <summary>Runs the installer (one UAC prompt). Device Selector opens during setup.</summary>
        public static Task<bool> InstallAsync(string setupPath)
        {
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            // Equalizer APO's Qt tools look for their plugins relative to the CURRENT directory.
            // An elevated PowerShell starts in System32, so the Device Selector that setup opens would fail
            // with "no Qt platform plugin". Run setup from the install folder instead.
            string apoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
            return RunElevatedAsync(
                $"New-Item -ItemType Directory -Force -Path {Q(apoDir)} | Out-Null\n" +
                $"Set-Location -LiteralPath {Q(apoDir)}\n" +
                $"$p = Start-Process -FilePath {Q(setupPath)} -ArgumentList '/S' -WorkingDirectory {Q(apoDir)} -Wait -PassThru\n" +
                GrantConfigAccess +
                "exit $p.ExitCode\n");
        }

        /// <summary>Opens Equalizer APO's Device Selector to attach it to more devices (one UAC prompt).</summary>
        public static Task<bool> SelectDevicesAsync()
        {
            string? tool = EqApoController.DeviceSelectorPath;
            if (tool == null) return Task.FromResult(false);
            return RunElevatedAsync(
                $"Set-Location -LiteralPath '{Path.GetDirectoryName(tool)!.Replace("'", "''")}'\n" +
                $"Start-Process -FilePath '{tool.Replace("'", "''")}' -WorkingDirectory '{Path.GetDirectoryName(tool)!.Replace("'", "''")}' -Wait\n" +
                GrantConfigAccess + "exit 0\n");
        }

        // ---------------- RNNoise (noise suppression for any microphone) ----------------

        // https://github.com/werman/noise-suppression-for-voice (GPL-3.0), official release asset
        private const string RnnoiseUrl = "https://github.com/werman/noise-suppression-for-voice/releases/download/v1.10/win-rnnoise.zip";
        private const string RnnoiseSha256 = "e4dcc186102850f8dcebeb766ad1bf34c24f6280bd614ef84b72dae4783fc517";

        /// <summary>Where the plugin lives (inside Equalizer APO's folder, readable by the audio engine).</summary>
        public static string RnnoisePath(bool stereo) =>
            Path.Combine(EqApoController.InstallDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO"),
                         "VSTPlugins", "IOCompanion", stereo ? "rnnoise_stereo.dll" : "rnnoise_mono.dll");

        public static bool RnnoiseInstalled => File.Exists(RnnoisePath(false)) && File.Exists(RnnoisePath(true));

        /// <summary>Downloads (SHA-256 verified) and installs the RNNoise VST plugin (one UAC prompt).</summary>
        public static async Task<bool> InstallRnnoiseAsync(IProgress<double>? progress = null)
        {
            string work = Path.Combine(Path.GetTempPath(), "IOCompanionRnnoise");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "win-rnnoise.zip");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SoundShot");
                using var response = await http.GetAsync(RnnoiseUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1, done = 0;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zip);
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read));
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (HashOf(zip) != RnnoiseSha256)
                throw new InvalidOperationException("Скачанный модуль шумоподавления не совпал с официальным. Установка отменена.");

            System.IO.Compression.ZipFile.ExtractToDirectory(zip, Path.Combine(work, "x"));
            string vst = Path.Combine(work, "x", "win-rnnoise", "vst");
            string target = Path.GetDirectoryName(RnnoisePath(false))!;
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            return await RunElevatedAsync(
                $"New-Item -ItemType Directory -Force -Path {Q(target)} | Out-Null\n" +
                $"Copy-Item -LiteralPath {Q(Path.Combine(vst, "rnnoise_mono.dll"))} -Destination {Q(target)} -Force\n" +
                $"Copy-Item -LiteralPath {Q(Path.Combine(vst, "rnnoise_stereo.dll"))} -Destination {Q(target)} -Force\n" +
                "exit 0\n") && RnnoiseInstalled;
        }

        public static void Reboot() =>
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"SoundShot: перезагрузка для включения эквалайзера\"")
            { CreateNoWindow = true, UseShellExecute = false });

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
                return false; // UAC declined
            }
            finally
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
}
