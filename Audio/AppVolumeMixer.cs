using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NAudio.CoreAudioApi;

namespace GraphiteCompanion.Audio
{
    public sealed class AppVolumeInfo
    {
        public string Process { get; init; } = "";   // process name without .exe (key for remembering)
        public string Title { get; init; } = "";
        public int Volume { get; set; }              // 0..100
        public bool Muted { get; set; }
        public bool IsSystemSounds { get; init; }
        public string? ExePath { get; init; }        // for the program icon
        /// <summary>What to show the icon of: for helper processes this is the main app's exe.</summary>
        public string? IconPath { get; set; }
    }

    /// <summary>Per-program volume on an output device (Windows audio sessions).</summary>
    public static class AppVolumeMixer
    {
        private static string ProcessName(uint pid)
        {
            try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
            catch { return ""; }
        }

        private static string? ExePathOf(uint pid)
        {
            try { using var p = Process.GetProcessById((int)pid); return p.MainModule?.FileName; }
            catch { return null; } // e.g. a program running as administrator
        }

        /// <summary>Icon path of a helper process: same folder, main app's exe (Apple Music plays through AMPLibraryAgent).</summary>
        private static string? IconPath(string? exePath, string processName)
        {
            if (exePath != null && MainAppExe.TryGetValue(processName, out string? mainExe))
            {
                try
                {
                    string candidate = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exePath)!, mainExe + ".exe");
                    if (System.IO.File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return exePath;
        }

        /// <summary>Clear names for common programs whose own description is odd ("nvcontainer", "Yandex with voice assistant Alice").</summary>
        private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["browser"] = "Яндекс Браузер", ["chrome"] = "Google Chrome", ["msedge"] = "Microsoft Edge",
            ["firefox"] = "Firefox", ["opera"] = "Opera", ["opera_gx"] = "Opera GX",
            ["Discord"] = "Discord", ["Telegram"] = "Telegram", ["Spotify"] = "Spotify",
            ["steam"] = "Steam", ["steamwebhelper"] = "Steam", ["nvcontainer"] = "NVIDIA (оверлей / ShadowPlay)",
            ["io_graphite_2_pro_wireless_audio_center_x64"] = "Audio Center (IO)", ["cs2"] = "Counter-Strike 2",
            ["dota2"] = "Dota 2", ["PBZ"] = "Phantom Blade Zero", ["vlc"] = "VLC", ["YandexMusic"] = "Яндекс Музыка",
            // Apple Music plays audio through its helper process; show the app the user knows
            ["AMPLibraryAgent"] = "Apple Music",
        };

        /// <summary>The session can belong to a helper process; for the icon, prefer the main app's exe.</summary>
        private static readonly Dictionary<string, string> MainAppExe = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AMPLibraryAgent"] = "AppleMusic",
        };

        private static string FriendlyTitle(uint pid, string fallback)
        {
            if (KnownNames.TryGetValue(fallback, out var known)) return known;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                string? d = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d!;
            }
            catch { }
            return fallback;
        }

        private static IEnumerable<(AudioSessionControl s, string name)> Sessions(string deviceId)
        {
            var device = new MMDeviceEnumerator().GetDevice(deviceId);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                string name = s.IsSystemSoundsSession ? "#system" : ProcessName(s.GetProcessID);
                if (name.Length == 0) continue;
                yield return (s, name);
            }
        }

        /// <summary>Programs that currently have an audio session on the device (one row per program).</summary>
        public static List<AppVolumeInfo> List(string deviceId)
        {
            var result = new Dictionary<string, AppVolumeInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var (s, name) in Sessions(deviceId))
            {
                if (result.ContainsKey(name)) continue;
                var vol = s.SimpleAudioVolume;
                result[name] = new AppVolumeInfo
                {
                    Process = name,
                    IsSystemSounds = s.IsSystemSoundsSession,
                    Title = s.IsSystemSoundsSession ? "Системные звуки" : FriendlyTitle(s.GetProcessID, name),
                    ExePath = s.IsSystemSoundsSession ? null : ExePathOf(s.GetProcessID),
                    IconPath = null,
                    Volume = (int)Math.Round(vol.Volume * 100),
                    Muted = vol.Mute,
                };
                if (result[name].ExePath != null)
                    result[name].IconPath = IconPath(result[name].ExePath, name);
            }
            return result.Values
                .Where(a => !a.Process.Equals(Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.IsSystemSounds).ThenBy(a => a.Title).ToList();
        }

        /// <summary>Sets volume (and optionally mute) for every session of the program on the device.</summary>
        public static void Set(string deviceId, string process, int? volume, bool? mute)
        {
            foreach (var (s, name) in Sessions(deviceId))
            {
                if (!name.Equals(process, StringComparison.OrdinalIgnoreCase)) continue;
                var v = s.SimpleAudioVolume;
                if (volume.HasValue) v.Volume = Math.Clamp(volume.Value, 0, 100) / 100f;
                if (mute.HasValue) v.Mute = mute.Value;
            }
        }

        /// <summary>Applies remembered volumes to programs that (re)started playing. Returns how many were changed.</summary>
        public static int ApplyRemembered(string deviceId, IReadOnlyDictionary<string, int> remembered)
        {
            int changed = 0;
            foreach (var (s, name) in Sessions(deviceId))
            {
                if (!remembered.TryGetValue(name, out int want)) continue;
                var v = s.SimpleAudioVolume;
                if (Math.Abs(v.Volume * 100 - want) > 0.6) { v.Volume = want / 100f; changed++; }
            }
            return changed;
        }
    }
}
