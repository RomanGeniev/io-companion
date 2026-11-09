using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GraphiteCompanion.Models;
using Microsoft.Win32;

namespace GraphiteCompanion.Services
{
    public sealed class CustomPreset
    {
        public string Name { get; set; } = "";
        public int[] Bands { get; set; } = new int[EqBands.Count];
        /// <summary>Optional effect state saved together with the preset ("format|pid" → value).</summary>
        public Dictionary<string, int>? Effects { get; set; }
    }

    public sealed class DeviceProfile
    {
        public ListeningMode Mode { get; set; } = ListeningMode.Music;
        public Earpad Earpad { get; set; } = Earpad.Fabric;
        public string? ActiveCustom { get; set; }
    }

    /// <summary>Foreground program → profile ("mode:Games" or "custom:Name").</summary>
    public sealed class AppRule
    {
        /// <summary>Process name without .exe (empty for title rules).</summary>
        public string Process { get; set; } = "";
        /// <summary>Text that must appear in the foreground window title (e.g. "Apple Music" in a browser).</summary>
        public string? Title { get; set; }
        public string Target { get; set; } = "mode:Games";

        public bool Matches(string process, string title) =>
            !string.IsNullOrEmpty(Title)
                ? title.Contains(Title, StringComparison.OrdinalIgnoreCase)
                : Process.Equals(process, StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public string Display => !string.IsNullOrEmpty(Title) ? $"«{Title}» в заголовке окна" : Process;
    }

    public sealed class AppSettings
    {
        public Earpad Earpad { get; set; } = Earpad.Fabric;
        public ListeningMode Mode { get; set; } = ListeningMode.Music;
        public string? ActiveCustom { get; set; }
        public bool EqEnabled { get; set; } = true;
        public Dictionary<string, int[]> Edited { get; set; } = new();
        public List<CustomPreset> Custom { get; set; } = new();
        public string? DeviceId { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool Notifications { get; set; } = true;

        public bool AutoSwitch { get; set; } = true;
        public List<AppRule> Rules { get; set; } = DefaultRules();
        public bool LinkEffects { get; set; } = true;
        public Dictionary<string, Dictionary<string, int>> ProfileEffects { get; set; } = new();
        public bool ClipGuard { get; set; } = true;
        public bool ShowOsd { get; set; } = true;
        public bool MicSounds { get; set; } = true;

        public bool Analyzer { get; set; } = true;
        /// <summary>Device id → "Headphones" / "Speakers" chosen by the user.</summary>
        public Dictionary<string, DeviceKind> DeviceKinds { get; set; } = new();

        public bool DiscordEnabled { get; set; }
        public string? DiscordAppId { get; set; }
        /// <summary>Remembered per-program volume (process name → 0..100).</summary>
        public Dictionary<string, int> AppVolumes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Device id → its own mode / earpads / preset.</summary>
        public Dictionary<string, DeviceProfile> DeviceProfiles { get; set; } = new();

        // screen recording / replays
        public bool ReplayEnabled { get; set; }
        public string? ReplayFolder { get; set; }
        public int ReplaySeconds { get; set; } = 60;
        public int RecordHeight { get; set; }          // 0 = as on screen, else 2160/1440/1080/720
        public int RecordFps { get; set; } = 60;
        public int RecordQuality { get; set; } = 1;    // 0 low, 1 medium, 2 high
        public string? RecordMonitor { get; set; }     // Screen.DeviceName, null = primary
        public bool RecordSystemSound { get; set; } = true;
        public bool RecordMicrophone { get; set; }

        public string Theme { get; set; } = "Dark";
        public string Accent { get; set; } = "Violet";
        public bool FullscreenMovies { get; set; } = true;
        public bool CheckUpdates { get; set; } = true;
        public string? SkippedVersion { get; set; }

        /// <summary>Bumped when new default rules are added, so existing users get them too.</summary>
        public int RulesVersion { get; set; }
        public const int CurrentRulesVersion = 3;

        public static List<AppRule> DefaultRules()
        {
            var list = new List<AppRule>();
            void Add(string target, params string[] processes)
            {
                foreach (var p in processes) list.Add(new AppRule { Process = p, Target = target });
            }
            void AddTitle(string target, params string[] titles)
            {
                foreach (var t in titles) list.Add(new AppRule { Title = t, Target = target });
            }
            Add("mode:Games", "cs2", "dota2", "VALORANT-Win64-Shipping", "r5apex", "FortniteClient-Win64-Shipping",
                "PUBG-Win64-Shipping", "RustClient", "GTA5", "Overwatch", "eldenring", "PBZ");
            AddTitle("mode:Games", "Phantom Blade");
            Add("mode:Music", "Spotify", "AIMP", "foobar2000", "YandexMusic", "AppleMusic");
            AddTitle("mode:Music", "Apple Music", "Яндекс Музыка", "YouTube Music", "Spotify");
            Add("mode:Movies", "vlc", "mpc-hc64", "PotPlayerMini64", "mpv", "Kinopoisk");
            AddTitle("mode:Movies", "Кинопоиск", "Netflix");
            return list;
        }

        /// <summary>Adds default rules that appeared in newer versions without touching the user's own ones.</summary>
        public void MigrateRules()
        {
            if (RulesVersion >= CurrentRulesVersion) return;
            foreach (var d in DefaultRules())
            {
                bool exists = Rules.Any(r =>
                    string.Equals(r.Process, d.Process, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Title ?? "", d.Title ?? "", StringComparison.OrdinalIgnoreCase));
                if (!exists) Rules.Add(d);
            }
            RulesVersion = CurrentRulesVersion;
        }
    }

    public static class SettingsStore
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GraphiteCompanion");

        private static readonly string FilePath = Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static bool Exists => File.Exists(FilePath);

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                    loaded.MigrateRules();
                    return loaded;
                }
            }
            catch
            {
                // Corrupted file: keep a copy and start fresh.
                try { File.Copy(FilePath, FilePath + ".bak", true); } catch { }
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
                File.Move(tmp, FilePath, true);
            }
            catch { /* not critical */ }
        }

        public static void LogError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "error.log"), $"[{DateTime.Now:O}] {ex}\n\n");
            }
            catch { }
        }
    }

    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "GraphiteCompanion";

        public static bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
        }

        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue(ValueName, false);
        }
    }
}
