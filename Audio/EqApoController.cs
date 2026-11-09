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
    /// folder; config.txt includes it. Every device gets its own "Device:" section, so speakers,
    /// headphones and microphones each keep their own sound. Equalizer APO applies changes on save.
    /// </summary>
    public sealed class EqApoController : IEqualizer
    {
        private static readonly int[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
        private const string OwnFile = "iocompanion.txt";
        private const string Marker = "# IO Companion";

        private readonly string deviceGuid;
        public string Engine => "Equalizer APO";

        private EqApoController(string deviceGuid) => this.deviceGuid = deviceGuid;

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
                    if (k?.GetValue("FriendlyName") is string n && n.Replace(" ", "").Contains("EqualizerAPO", StringComparison.OrdinalIgnoreCase))
                        set.Add(id);
                }
            }
            catch { }
            return set;
        }

        /// <summary>Is Equalizer APO attached to this device (ticked in Device Selector)? Works for outputs and microphones.</summary>
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

        /// <summary>"{0.0.0.00000000}.{guid}" → "{guid}" (what Equalizer APO's Device: command matches).</summary>
        public static string GuidOf(string deviceId)
        {
            int dot = deviceId.LastIndexOf('.');
            return dot >= 0 ? deviceId[(dot + 1)..] : deviceId;
        }

        /// <summary>Opens the controller when Equalizer APO is installed, attached to the device and its config is writable.</summary>
        public static EqApoController? TryOpen(string deviceId)
        {
            if (!IsAttached(deviceId) || ConfigDir is not { } dir || !Directory.Exists(dir)) return null;
            try
            {
                lock (Store)
                {
                    Store.EnsureLoaded(dir);
                    Store.EnsureIncluded();
                    string guid = GuidOf(deviceId);
                    Store.Out.TryAdd(guid, new OutSection());
                    Store.Save();
                    return new EqApoController(guid);
                }
            }
            catch (UnauthorizedAccessException)
            {
                return null; // config folder not writable (permissions are granted by our installer step)
            }
        }

        // ---------------- IEqualizer (this device's section) ----------------

        private OutSection Section
        {
            get
            {
                if (!Store.Out.TryGetValue(deviceGuid, out var s)) Store.Out[deviceGuid] = s = new OutSection();
                return s;
            }
        }

        public int[] ReadBands() { lock (Store) return (int[])Section.Bands.Clone(); }

        public bool ReadEnabled() { lock (Store) return Section.Enabled; }

        // what is on disk; on a failed write it stays "dirty", so the next call retries
        private bool dirty = true;

        public void SetEnabled(bool on)
        {
            lock (Store)
            {
                if (Section.Enabled == on && !dirty) return;
                Section.Enabled = on;
                dirty = true;
                Store.Save();
                dirty = false;
            }
        }

        public void SetBands(IReadOnlyList<int> newBands, IReadOnlyList<int>? previous)
        {
            var b = newBands.Select(v => Math.Clamp(v, EqBands.Min, EqBands.Max)).ToArray();
            lock (Store)
            {
                if (b.SequenceEqual(Section.Bands) && !dirty) return;
                Section.Bands = b;
                dirty = true;
                Store.Save();
                dirty = false;
            }
        }

        public void Dispose() { }

        // ---------------- microphone noise suppression ----------------

        /// <summary>Turns RNNoise on/off for a microphone that has Equalizer APO attached. Null plugin path = off.</summary>
        public static bool SetMicNoiseSuppression(string micDeviceId, string? pluginPath)
        {
            if (!IsAttached(micDeviceId) || ConfigDir is not { } dir) return false;
            lock (Store)
            {
                Store.EnsureLoaded(dir);
                Store.EnsureIncluded();
                string guid = GuidOf(micDeviceId);
                if (pluginPath == null) Store.Mic.Remove(guid);
                else Store.Mic[guid] = pluginPath;
                Store.Save();
            }
            return true;
        }

        public static bool MicNoiseSuppressionOn(string micDeviceId)
        {
            if (ConfigDir is not { } dir) return false;
            lock (Store)
            {
                Store.EnsureLoaded(dir);
                return Store.Mic.ContainsKey(GuidOf(micDeviceId));
            }
        }

        // ---------------- shared config file ----------------

        private sealed class OutSection
        {
            public bool Enabled = true;
            public int[] Bands = new int[EqBands.Count];
        }

        private static readonly ConfigStore Store = new();

        private sealed class ConfigStore
        {
            public readonly Dictionary<string, OutSection> Out = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> Mic = new(StringComparer.OrdinalIgnoreCase); // guid → plugin dll
            private string? dir;

            private string OwnPath => Path.Combine(dir!, OwnFile);

            public void EnsureLoaded(string configDir)
            {
                if (dir == configDir) return;
                dir = configDir;
                Out.Clear();
                Mic.Clear();
                if (!File.Exists(OwnPath)) return;
                // state lines: "# out {guid} enabled=1 bands=0,1,..." / "# mic {guid} plugin=C:\...\x.dll"
                // (older single-device format "# enabled=" / "# bands=" is dropped - sections start fresh)
                foreach (var line in File.ReadAllLines(OwnPath))
                {
                    var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || parts[0] != "#") continue;
                    if (parts[1] == "out" && parts.Length >= 4)
                    {
                        var s = new OutSection();
                        foreach (var kv in parts[3].Split(' '))
                        {
                            if (kv.StartsWith("enabled=")) s.Enabled = kv.EndsWith("1");
                            else if (kv.StartsWith("bands="))
                            {
                                var b = kv[6..].Split(',');
                                if (b.Length == EqBands.Count)
                                    s.Bands = b.Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
                            }
                        }
                        Out[parts[2]] = s;
                    }
                    else if (parts[1] == "mic" && parts.Length >= 4 && parts[3].StartsWith("plugin="))
                    {
                        Mic[parts[2]] = parts[3][7..];
                    }
                }
            }

            /// <summary>Makes config.txt include our file (the original config is kept as a backup).</summary>
            public void EnsureIncluded()
            {
                string main = Path.Combine(dir!, "config.txt");
                string include = $"Include: {OwnFile}";
                if (File.Exists(main))
                {
                    string text = File.ReadAllText(main);
                    if (text.Contains(include, StringComparison.OrdinalIgnoreCase)) return;
                    string backup = Path.Combine(dir!, "config.before-iocompanion.txt");
                    if (!File.Exists(backup)) File.Copy(main, backup);
                }
                WriteWithRetry(main, Encoding.UTF8.GetBytes(
                    $"{Marker}: the equalizer is controlled by IO Companion.\r\n" +
                    "# Your previous config is saved as config.before-iocompanion.txt\r\n" +
                    include + "\r\n"));
            }

            public void Save()
            {
                var ic = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append(Marker).Append(" - written automatically, changes here will be overwritten\r\n");
                foreach (var (guid, s) in Out)
                    sb.Append($"# out {guid} enabled={(s.Enabled ? 1 : 0)} bands={string.Join(",", s.Bands.Select(b => b.ToString(ic)))}\r\n");
                foreach (var (guid, plugin) in Mic)
                    sb.Append($"# mic {guid} plugin={plugin}\r\n");

                foreach (var (guid, s) in Out)
                {
                    sb.Append($"\r\nDevice: {guid}\r\n");
                    if (s.Enabled)
                        sb.Append("GraphicEQ: ")
                          .Append(string.Join("; ", Frequencies.Select((f, i) => $"{f.ToString(ic)} {s.Bands[i].ToString(ic)}")))
                          .Append("\r\n");
                }
                foreach (var (guid, plugin) in Mic)
                    sb.Append($"\r\nDevice: {guid}\r\nVSTPlugin: Library \"{plugin}\"\r\n");
                // anything Equalizer APO is attached to but we don't manage: leave untouched (no commands)
                WriteWithRetry(OwnPath, Encoding.UTF8.GetBytes(sb.ToString()));
            }
        }

        /// <summary>
        /// Equalizer APO (inside the Windows audio engine) re-reads the file right after every change and
        /// keeps it open for a moment; a write at that instant is refused. Retry briefly instead of failing.
        /// </summary>
        private static void WriteWithRetry(string path, byte[] data)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    fs.Write(data, 0, data.Length);
                    break;
                }
                catch (IOException) when (attempt < 25) { System.Threading.Thread.Sleep(20); }
                catch (UnauthorizedAccessException) when (attempt < 25 && File.Exists(path)) { System.Threading.Thread.Sleep(20); }
            }
            try { File.Delete(path + ".tmp"); } catch { }
        }
    }
}
