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
    }

    /// <summary>Per-program volume on an output device (Windows audio sessions).</summary>
    public static class AppVolumeMixer
    {
        private static string ProcessName(uint pid)
        {
            try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
            catch { return ""; }
        }

        private static string FriendlyTitle(uint pid, string fallback)
        {
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
                    Volume = (int)Math.Round(vol.Volume * 100),
                    Muted = vol.Mute,
                };
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
