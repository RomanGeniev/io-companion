using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GraphiteCompanion.Models;
using Microsoft.Win32;

namespace GraphiteCompanion.Audio
{
    /// <summary>
    /// Equalizer through Equalizer APO (open source, works with any output device).
    /// IO Companion keeps its settings in its own file "iocompanion.txt" inside Equalizer APO's config
    /// folder and config.txt includes it; Equalizer APO applies changes as soon as the file is saved.
    /// </summary>
    public sealed class EqApoController : IEqualizer
    {
        private static readonly int[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        private const string OwnFile = "iocompanion.txt";
        private const string Marker = "# IO Companion";

        private readonly string configDir;
        private int[] bands = new int[EqBands.Count];
        private bool enabled = true;

        public string Engine => "Equalizer APO";

        private EqApoController(string configDir)
        {
            this.configDir = configDir;
            Load();
        }

        // ---------------- detection ----------------

        public static string? InstallDir
        {
            get
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                    if (k?.GetValue("InstallPath") is string p && Directory.Exists(p)) return p;
                }
                catch { }
                string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO");
                return File.Exists(Path.Combine(def, "EqualizerAPO.dll")) ? def : null;
            }
        }

        public static bool Installed => InstallDir != null;

        public static string? ConfigDir
        {
            get
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                    if (k?.GetValue("ConfigPath") is string p && Directory.Exists(p)) return p;
                }
                catch { }
                var dir = InstallDir;
                return dir == null ? null : Path.Combine(dir, "config");
            }
        }

        /// <summary>The device-selection tool shipped with Equalizer APO (DeviceSelector in 1.4+, Configurator before).</summary>
        public static string? DeviceSelectorPath
        {
            get
            {
                var dir = InstallDir;
                if (dir == null) return null;
                foreach (var name in new[] { "DeviceSelector.exe", "Configurator.exe" })
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        /// <summary>CLSIDs under which Equalizer APO registered its audio processing objects.</summary>
        private static HashSet<string> ApoClsids()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes\AudioEngine\AudioProcessingObjects");
                if (root == null) return set;
                foreach (var id in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(id);
                    if (k?.GetValue("FriendlyName") is string n && n.Contains("Equalizer APO", StringComparison.OrdinalIgnoreCase))
                        set.Add(id);
                }
            }
            catch { }
            return set;
        }

        /// <summary>Is Equalizer APO attached to this output device (selected in Device Selector)?</summary>
        public static bool IsAttached(string deviceId)
        {
            var path = ApoPropertyStore.FxRegistryPath(deviceId);
            if (path == null || !Installed) return false;
            var ids = ApoClsids();
            if (ids.Count == 0) return false;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(path);
                if (k == null) return false;
                foreach (var name in k.GetValueNames())
                {
                    switch (k.GetValue(name))
                    {
                        case string s when ids.Contains(s.Trim()):
                            return true;
                        case string[] arr when arr.Any(a => ids.Contains(a.Trim())):
                            return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Opens the controller when Equalizer APO is installed, attached to the device and its config is writable.</summary>
        public static EqApoController? TryOpen(string deviceId)
        {
            if (!IsAttached(deviceId)) return null;
            var dir = ConfigDir;
            if (dir == null || !Directory.Exists(dir)) return null;
            try
            {
                var c = new EqApoController(dir);
                c.EnsureIncluded();
                c.Save();
                return c;
            }
            catch (UnauthorizedAccessException)
            {
                return null; // config folder not writable (permissions are granted by our installer step)
            }
        }

        // ---------------- config files ----------------

        private string OwnPath => Path.Combine(configDir, OwnFile);

        /// <summary>Makes config.txt include our file (the original config is kept as a backup).</summary>
        private void EnsureIncluded()
        {
            string main = Path.Combine(configDir, "config.txt");
            string include = $"Include: {OwnFile}";
            if (File.Exists(main))
            {
                string text = File.ReadAllText(main);
                if (text.Contains(include, StringComparison.OrdinalIgnoreCase)) return;
                string backup = Path.Combine(configDir, "config.before-iocompanion.txt");
                if (!File.Exists(backup)) File.Copy(main, backup);
            }
            File.WriteAllText(main,
                $"{Marker}: the equalizer is controlled by IO Companion.\r\n" +
                "# Your previous config is saved as config.before-iocompanion.txt\r\n" +
                include + "\r\n", Encoding.UTF8);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(OwnPath)) return;
                foreach (var line in File.ReadAllLines(OwnPath))
                {
                    if (line.StartsWith("# enabled=", StringComparison.Ordinal))
                        enabled = line.EndsWith("1");
                    else if (line.StartsWith("# bands=", StringComparison.Ordinal))
                    {
                        var parts = line[8..].Split(',');
                        if (parts.Length == EqBands.Count)
                            bands = parts.Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
                    }
                }
            }
            catch { }
        }

        private void Save()
        {
            var sb = new StringBuilder();
            sb.Append(Marker).Append(" - written automatically, changes here will be overwritten\r\n");
            sb.Append("# enabled=").Append(enabled ? 1 : 0).Append("\r\n");
            sb.Append("# bands=").Append(string.Join(",", bands.Select(b => b.ToString(CultureInfo.InvariantCulture)))).Append("\r\n");
            if (enabled)
            {
                sb.Append("GraphicEQ: ");
                sb.Append(string.Join("; ", Frequencies.Select((f, i) =>
                    $"{f.ToString(CultureInfo.InvariantCulture)} {bands[i].ToString(CultureInfo.InvariantCulture)}")));
                sb.Append("\r\n");
            }
            // write to a temp file first so Equalizer APO never reads a half-written config
            string tmp = OwnPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            File.Move(tmp, OwnPath, true);
        }

        // ---------------- IEqualizer ----------------

        public int[] ReadBands() => (int[])bands.Clone();

        public bool ReadEnabled() => enabled;

        public void SetEnabled(bool on)
        {
            if (enabled == on) return;
            enabled = on;
            Save();
        }

        public void SetBands(IReadOnlyList<int> newBands, IReadOnlyList<int>? previous)
        {
            var b = newBands.Select(v => Math.Clamp(v, EqBands.Min, EqBands.Max)).ToArray();
            if (b.SequenceEqual(bands)) return;
            bands = b;
            Save();
        }

        public void Dispose() { }
    }
}
