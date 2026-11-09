using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GraphiteCompanion.Services;
using WF = System.Windows.Forms;

namespace GraphiteCompanion
{
    // Replays (last N seconds by a hotkey) and manual screen recording.
    public partial class MainWindow
    {
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        private sealed class MonitorItem
        {
            public string Device = "";
            public string Label { get; init; } = "";
            public WF.Screen Screen = null!;
        }

        private readonly ReplayService replay = new();
        private DispatcherTimer? recTimer;
        private bool recBusy;
        private string replayKey = "Alt + F10", recordKey = "Alt + F9", shotKey = "Alt + F8";

        private static readonly int[] Durations = { 15, 30, 60, 120, 300 };
        private static readonly int[] Heights = { 0, 2160, 1440, 1080, 720 };
        private static readonly int[] Fpss = { 30, 60, 120 };

        private string RecFolder =>
            settings.ReplayFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IO Companion");

        private void InitRecord()
        {
            Segments(DurationGrid, Durations, d => d < 60 ? $"{d} с" : $"{d / 60} мин", settings.ReplaySeconds, v => settings.ReplaySeconds = v);
            Segments(FpsGrid, Fpss, f => $"{f}", settings.RecordFps, v => settings.RecordFps = v);
            Segments(QualityGrid, new[] { 0, 1, 2 }, q => q switch { 0 => "Низкое", 1 => "Среднее", _ => "Высокое" },
                     settings.RecordQuality, v => settings.RecordQuality = v);

            var monitors = WF.Screen.AllScreens.Select((s, i) => new MonitorItem
            {
                Device = s.DeviceName, Screen = s,
                Label = $"Монитор {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (основной)" : ""),
            }).ToList();
            MonitorCombo.ItemsSource = monitors;
            MonitorCombo.SelectedItem = monitors.FirstOrDefault(m => m.Device == settings.RecordMonitor) ?? monitors.FirstOrDefault(m => m.Screen.Primary);
            BuildResolutions();

            RecSystemSwitch.IsChecked = settings.RecordSystemSound;
            RecMicSwitch.IsChecked = settings.RecordMicrophone;
            ReplaySwitch.IsChecked = settings.ReplayEnabled;
            RecFolderText.Text = RecFolder;

            UpdateHotkeyTexts();

            recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            recTimer.Tick += (_, _) => { TrackForeground(); UpdateRecStatus(); };
            recTimer.Start();

            if (settings.ReplayEnabled && FfmpegInstaller.Installed) _ = StartReplayBuffer();
            UpdateRecStatus();
        }

        private string RegFirst(Action action, params (uint mods, uint key, string label)[] options)
        {
            foreach (var o in options)
                if (hotkeys.Register(o.mods, o.key, action)) return o.label;
            return "не назначено";
        }

        private void Segments(UniformGrid grid, int[] values, Func<int, string> label, int current, Action<int> set)
        {
            grid.Children.Clear();
            string group = grid.Name;
            foreach (int v in values)
            {
                int value = v;
                var rb = new RadioButton { GroupName = group, Content = label(v), IsChecked = v == current };
                rb.SetResourceReference(StyleProperty, "Segment");
                rb.Click += (_, _) => { set(value); RecSettingsChanged(); };
                grid.Children.Add(rb);
            }
        }

        /// <summary>Resolution choices up to the selected monitor's own height.</summary>
        private void BuildResolutions()
        {
            int native = (MonitorCombo.SelectedItem as MonitorItem)?.Screen.Bounds.Height ?? 1080;
            var heights = Heights.Where(h => h == 0 || h < native).ToArray();
            if (!heights.Contains(settings.RecordHeight)) settings.RecordHeight = 0;
            ResolutionGrid.Columns = heights.Length;
            Segments(ResolutionGrid, heights, h => h == 0 ? $"Как на экране ({native}p)" : $"{h}p", settings.RecordHeight, v => settings.RecordHeight = v);
        }

        private RecordOptions BuildOptions()
        {
            var screen = (MonitorCombo.SelectedItem as MonitorItem)?.Screen ?? WF.Screen.PrimaryScreen!;
            var b = screen.Bounds;
            int h = settings.RecordHeight, w = 0;
            if (h > 0) w = (int)Math.Round(h * (double)b.Width / b.Height / 2) * 2;
            return new RecordOptions
            {
                Monitor = MonitorFromPoint(new System.Drawing.Point(b.Left + b.Width / 2, b.Top + b.Height / 2), 2),
                Width = w, Height = h,
                Fps = settings.RecordFps, Quality = settings.RecordQuality,
                SystemSound = settings.RecordSystemSound, Microphone = settings.RecordMicrophone,
                ReplaySeconds = settings.ReplaySeconds, Folder = RecFolder,
            };
        }

        private void RecSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized) return;
            if (sender == MonitorCombo && MonitorCombo.SelectedItem is MonitorItem m)
            {
                settings.RecordMonitor = m.Device;
                BuildResolutions();
            }
            settings.RecordSystemSound = RecSystemSwitch.IsChecked == true;
            settings.RecordMicrophone = RecMicSwitch.IsChecked == true;
            RecSettingsChanged();
        }

        /// <summary>New settings apply to the replay buffer right away (a running recording keeps its own).</summary>
        private void RecSettingsChanged()
        {
            ScheduleSave();
            if (replay.BufferRunning) _ = StartReplayBuffer();
            UpdateRecStatus();
        }

        /// <summary>Downloads FFmpeg on first use and picks the encoder. Returns false if that failed.</summary>
        private async System.Threading.Tasks.Task<bool> EnsureRecorderReady()
        {
            if (!FfmpegInstaller.Installed)
            {
                RecStatus.Text = "Скачиваю движок записи FFmpeg (один раз, ~110 МБ)…";
                try
                {
                    await FfmpegInstaller.InstallAsync(new Progress<double>(p => RecStatus.Text = $"Скачиваю движок записи FFmpeg… {p:P0}"));
                }
                catch (Exception ex)
                {
                    SettingsStore.LogError(ex);
                    RecStatus.Text = "Не удалось скачать FFmpeg: " + ex.Message;
                    return false;
                }
            }
            if (replay.Codec == ReplayService.Encoder.None)
            {
                RecStatus.Text = "Проверяю видеокарту…";
                await replay.DetectEncoderAsync();
            }
            return true;
        }

        private async System.Threading.Tasks.Task StartReplayBuffer()
        {
            if (!await EnsureRecorderReady()) { ReplaySwitch.IsChecked = settings.ReplayEnabled = false; return; }
            try { replay.StartBuffer(BuildOptions()); }
            catch (Exception ex) { SettingsStore.LogError(ex); RecStatus.Text = "Не удалось запустить повторы: " + ex.Message; }
            UpdateRecStatus();
        }

        private async void ReplaySwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.ReplayEnabled = ReplaySwitch.IsChecked == true;
            ScheduleSave();
            if (settings.ReplayEnabled) await StartReplayBuffer();
            else replay.StopBuffer();
            UpdateRecStatus();
        }

        // ---------------- which game a file belongs to ----------------

        private const string Desktop = "Рабочий стол";
        private static readonly string[] ShellApps =
        {
            "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "LockApp", "TextInputHost", "ApplicationFrameHost", "ShellHost", "dwm", "Taskmgr",
        };
        private static readonly string[] GenericNames = { "application", "launcher", "game", "unity player" };
        private readonly Dictionary<uint, string?> appNames = new();
        private string? lastGame;                              // last real program that was in front (not IO Companion)
        private string startGame = Desktop;
        private readonly Dictionary<string, int> recSeconds = new();

        /// <summary>(program name, whether the front window is IO Companion itself). Name is null for the desktop/taskbar.</summary>
        private (string? name, bool self) ForegroundApp()
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
                if (pid == 0) return (null, false);
                if (pid == Environment.ProcessId) return (null, true);
                if (appNames.TryGetValue(pid, out var cached)) return (cached, false);
                string? name = null;
                using (var p = Process.GetProcessById((int)pid))
                {
                    if (!ShellApps.Any(s => s.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase)))
                    {
                        string? d = null;
                        try { d = p.MainModule?.FileVersionInfo.FileDescription?.Trim(); } catch { } // protected / elevated games
                        if (d != null && d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) d = d[..^4];
                        name = string.IsNullOrWhiteSpace(d) || GenericNames.Contains(d.ToLowerInvariant()) ? p.ProcessName : d;
                    }
                }
                if (appNames.Count > 64) appNames.Clear();
                appNames[pid] = name;
                return (name, false);
            }
            catch { return (null, false); }
        }

        /// <summary>The program a new file belongs to. Pressed from IO Companion itself → the program used just before.</summary>
        private string CurrentGame()
        {
            var (name, self) = ForegroundApp();
            if (name != null) return name;
            return self && lastGame != null ? lastGame : Desktop;
        }

        /// <summary>Once a second: remember the last program and count how long each one was in front during a recording.</summary>
        private void TrackForeground()
        {
            var (name, _) = ForegroundApp();
            if (name == null) return;
            lastGame = name;
            if (replay.Recording) recSeconds[name] = recSeconds.GetValueOrDefault(name) + 1;
        }

        /// <summary>
        /// Alt-Tab does not interrupt a recording (it captures the whole monitor) and never splits the file.
        /// The folder is the program the recording was started in; if it was started on the desktop or in a
        /// launcher-less moment, the program that was in front the longest.
        /// </summary>
        private string GameForRecording()
        {
            if (startGame != Desktop || recSeconds.Count == 0) return startGame;
            return recSeconds.MaxBy(kv => kv.Value).Key;
        }

        private bool shotBusy;

        public async System.Threading.Tasks.Task TakeScreenshot()
        {
            if (shotBusy) return;
            shotBusy = true;
            try
            {
                var o = BuildOptions();
                var bounds = (MonitorCombo.SelectedItem as MonitorItem)?.Screen.Bounds ?? WF.Screen.PrimaryScreen!.Bounds;
                var hwnd = GetForegroundWindow();
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (hwnd != IntPtr.Zero && pid != Environment.ProcessId)
                {
                    // the monitor the game is on, not necessarily the one selected for recording
                    var screen = WF.Screen.FromHandle(hwnd);
                    bounds = screen.Bounds;
                    o.Monitor = MonitorFromPoint(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 2);
                }
                string file = await replay.ScreenshotAsync(o, CurrentGame(), bounds);
                NotifyChange("Скриншот сохранён", "", Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NotifyChange("Не удалось сделать скриншот", "", ex.Message);
            }
            finally { shotBusy = false; }
        }

        private void Screenshot_Click(object sender, RoutedEventArgs e) => _ = TakeScreenshot();

        public async System.Threading.Tasks.Task SaveReplay()
        {
            if (recBusy) return;
            if (!replay.BufferRunning)
            {
                NotifyChange("Повторы выключены", "", "Включите их на вкладке «Запись»");
                return;
            }
            recBusy = true;
            SaveReplayText.Text = "Сохраняю…";
            try
            {
                string file = await replay.SaveReplayAsync(CurrentGame());
                NotifyChange("Повтор сохранён", "", Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NotifyChange("Не удалось сохранить повтор", "", ex.Message);
            }
            finally
            {
                recBusy = false;
                SaveReplayText.Text = "Сохранить повтор";
                UpdateRecStatus();
            }
        }

        public async System.Threading.Tasks.Task ToggleRecording()
        {
            if (recBusy) return;
            recBusy = true;
            try
            {
                if (replay.Recording)
                {
                    RecordText.Text = "Сохраняю…";
                    string? file = await replay.StopRecordingAsync(GameForRecording());
                    NotifyChange("Запись сохранена", "", file != null ? Path.GetFileName(file) : null);
                }
                else
                {
                    if (!await EnsureRecorderReady()) return;
                    startGame = CurrentGame();
                    recSeconds.Clear();
                    replay.StartRecording(BuildOptions(), startGame);
                    NotifyChange("Запись началась", "", $"{recordKey} — остановить");
                }
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NotifyChange("Ошибка записи", "", ex.Message);
            }
            finally
            {
                recBusy = false;
                UpdateRecStatus();
            }
        }

        private void SaveReplay_Click(object sender, RoutedEventArgs e) => _ = SaveReplay();
        private void Record_Click(object sender, RoutedEventArgs e) => _ = ToggleRecording();

        private void OpenRecFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(RecFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RecFolder}\"") { UseShellExecute = true });
        }

        private void ChooseRecFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Куда сохранять ролики", InitialDirectory = RecFolder };
            if (dlg.ShowDialog(this) != true) return;
            settings.ReplayFolder = dlg.FolderName;
            RecFolderText.Text = RecFolder;
            RecSettingsChanged();
        }

        public bool RecordingActive => replay.Recording;
        public bool ReplayActive => replay.BufferRunning;

        private void UpdateRecStatus()
        {
            if (RecStatus == null || recBusy && RecStatus.Text.StartsWith("Скачиваю")) return;
            string enc = replay.Codec == ReplayService.Encoder.None ? "" : " · кодирует " + ReplayService.EncoderName(replay.Codec);
            if (replay.Recording)
            {
                var t = DateTime.Now - replay.RecordingSince;
                RecordText.Text = $"Остановить  {(int)t.TotalMinutes:00}:{t.Seconds:00}";
            }
            else if (!recBusy) RecordText.Text = "Начать запись";

            if (replay.BufferRunning)
                RecStatus.Text = $"Повторы включены: держу последние {DurationText(settings.ReplaySeconds)}{enc}. " +
                                 $"Временный буфер на диске: {ReplayService.BufferBytes() / (1024 * 1024)} МБ.";
            else if (settings.ReplayEnabled && !FfmpegInstaller.Installed)
                RecStatus.Text = "Включите переключатель — программа один раз скачает движок записи.";
            else if (!recBusy)
                RecStatus.Text = "Повторы выключены. Включите — и любой момент последних секунд можно сохранить одной клавишей." +
                                 (enc.Length > 0 ? enc : "");
            SaveReplayButton.IsEnabled = replay.BufferRunning && !recBusy;

            RecInfo.Text = (FfmpegInstaller.Installed ? "" : "При первом включении скачается FFmpeg (~110 МБ) — открытый движок записи, как в OBS. ") +
                           "Видеокарта сама сжимает видео (NVIDIA / AMD / Intel), поэтому игра почти не проседает. " +
                           "Защищённое видео (Netflix, Кинопоиск) Windows записывает чёрным экраном.";
        }

        private static string DurationText(int s) => s < 60 ? $"{s} секунд" : s == 60 ? "1 минуту" : s < 300 ? $"{s / 60} минуты" : $"{s / 60} минут";
    }
}
