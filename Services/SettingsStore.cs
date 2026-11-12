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
    /// <summary>Recording settings of one monitor.</summary>
    public sealed class MonitorRecordSettings
    {
        public int Height { get; set; }        // 0 = as on screen
        public int Fps { get; set; } = 60;
        public int Quality { get; set; } = 1;  // 0 low, 1 medium, 2 high
    }

    public sealed class CustomPreset
    {
        public string Name { get; set; } = "";
        public int[] Bands { get; set; } = new int[EqBands.Count];
    }

    public sealed class DeviceProfile
    {
        public ListeningMode Mode { get; set; } = ListeningMode.Music;
        public Earpad Earpad { get; set; } = Earpad.Fabric;
        public string? ActiveCustom { get; set; }
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
        public string? MicrophoneId { get; set; }
        public string? MicrophoneName { get; set; }
        public bool MinimizeToTray { get; set; } = true;
        public bool SidebarCollapsed { get; set; }
        public int NavigationLayoutVersion { get; set; }
        public List<NavigationItem>? Navigation { get; set; }
        public bool Notifications { get; set; } = true;

        /// <summary>Hotkeys changed by the user: action id → "modifiers:virtualKey".</summary>
        public Dictionary<string, string> Hotkeys { get; set; } = new();

        public bool ClipGuard { get; set; } = true;
        public bool ShowOsd { get; set; } = true;
        public bool MicSounds { get; set; } = true;

        public bool Analyzer { get; set; } = true;
        /// <summary>Device id → "Headphones" / "Speakers" chosen by the user.</summary>
        public Dictionary<string, DeviceKind> DeviceKinds { get; set; } = new();

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
        public string? RecordMonitor { get; set; }     // legacy single monitor, migrated to RecordMonitors
        public List<string>? RecordMonitors { get; set; } // Screen.DeviceName of every monitor to record, null = primary only
        public Dictionary<string, MonitorRecordSettings>? RecordPerMonitor { get; set; } // Screen.DeviceName → own resolution / fps / quality
        public bool RecordSystemSound { get; set; } = true;
        public bool RecordMicrophone { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RecordMicrophoneDeviceId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RecordMicrophoneName { get; set; }

        public string Theme { get; set; } = "Dark";
        public string Accent { get; set; } = "Violet";
        public string CustomAccent { get; set; } = "#22D3EE";
        public bool PushToTalk { get; set; }
        public AutoClickerSettings? AutoClicker { get; set; } = new();
        public OverlayLayout? Overlay { get; set; } = new();
        public Dictionary<string, CanvasLayout>? CanvasLayouts { get; set; } = new();
        public Dictionary<string, List<GraphiteCompanion.Controls.ViewBlockSetting>>? ViewBlocks { get; set; } = new();
        public bool CheckUpdates { get; set; } = true;
        public string? SkippedVersion { get; set; }

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
        private const string ValueName = "SoundShot";
        private const string LegacyValueName = "GraphiteCompanion";

        public static bool IsEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string || key?.GetValue(LegacyValueName) is string;
            }
        }

        public static void Set(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else
                key.DeleteValue(ValueName, false);
            // Keep existing preferences while migrating the old startup name.
            key.DeleteValue(LegacyValueName, false);
        }
    }
}
