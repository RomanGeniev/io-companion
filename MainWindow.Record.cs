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

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int L, T, R, B; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out WinRect r);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLongStyle(IntPtr h, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

        private sealed class MonitorItem
        {
            public string Device = "";
            public int Index;
            public string Label { get; init; } = "";
            public WF.Screen Screen = null!;
            public IntPtr Handle;
        }

        private List<MonitorItem> monitors = new();
        private readonly Dictionary<string, ReplayService> svcs = new();   // one recorder per monitor
        private IEnumerable<MonitorItem> SelectedMonitors => monitors.Where(m => settings.RecordMonitors?.Contains(m.Device) == true);

        private ReplayService Svc(MonitorItem m)
        {
            if (!svcs.TryGetValue(m.Device, out var s)) svcs[m.Device] = s = new ReplayService(m.Device.TrimStart('\\', '.'));
            s.MonitorName = $"Монитор {m.Index + 1}";
            return s;
        }

        private bool AnyRecording => svcs.Values.Any(s => s.Recording);
        private bool AnyBuffer => svcs.Values.Any(s => s.BufferRunning);
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

            monitors = WF.Screen.AllScreens.Select((s, i) => new MonitorItem
            {
                Device = s.DeviceName, Screen = s, Index = i,
                Label = $"Монитор {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (основной)" : ""),
                Handle = MonitorFromPoint(new System.Drawing.Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2), 2),
            }).ToList();
            // older versions stored one monitor; now it is a list (at least one is always selected)
            var sel = (settings.RecordMonitors ?? (settings.RecordMonitor != null ? new List<string> { settings.RecordMonitor } : new()))
                .Where(d => monitors.Any(m => m.Device == d)).Distinct().ToList();
            if (sel.Count == 0) sel.Add((monitors.FirstOrDefault(m => m.Screen.Primary) ?? monitors[0]).Device);
            settings.RecordMonitors = sel;
            MonitorPanel.Children.Clear();
            foreach (var m in monitors)
            {
                var cb = new CheckBox { Content = m.Label, IsChecked = sel.Contains(m.Device), Tag = m };
                cb.SetResourceReference(StyleProperty, "Switch");
                cb.Click += MonitorCheck_Click;
                MonitorPanel.Children.Add(cb);
            }
            UpdateMonitorHint();
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

        private void MonitorCheck_Click(object sender, RoutedEventArgs e)
        {
            var cb = (CheckBox)sender;
            var list = MonitorPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => ((MonitorItem)c.Tag).Device).ToList();
            if (list.Count == 0) { cb.IsChecked = true; list.Add(((MonitorItem)cb.Tag).Device); } // can't record nothing
            settings.RecordMonitors = list;
            UpdateMonitorHint();
            BuildResolutions();
            RecSettingsChanged();
        }

        private void UpdateMonitorHint()
        {
            MonitorHint.Text = SelectedMonitors.Count() > 1
                ? "По клавише записи, повтора или скриншота появится выбор: какой монитор сохранить (или все). " +
                  "Файлы лягут в «Монитор N\\Видео\\Игра» и «Монитор N\\Фото\\Игра»."
                : "Файлы лягут в «Монитор N\\Видео\\Игра» и «Монитор N\\Фото\\Игра». Игра определяется по окну на этом мониторе.";
        }

        /// <summary>
        /// Which of the selected monitors to save. One selected → that one, no question.
        /// Several → a picker over the game, pre-highlighting the monitor with the active window. Null = cancelled.
        /// </summary>
        private async System.Threading.Tasks.Task<List<MonitorItem>?> ChooseMonitors(string title, string hint)
        {
            var sel = SelectedMonitors.ToList();
            if (sel.Count <= 1) return sel;
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint pid);
            var fgMon = fg != IntPtr.Zero && pid != Environment.ProcessId ? MonitorFromWindow(fg, 2) : IntPtr.Zero;
            var def = sel.FirstOrDefault(m => m.Handle == fgMon) ?? sel.FirstOrDefault(m => m.Screen.Primary) ?? sel[0];
            var choices = sel.Select(m => new Controls.MonitorPickWindow.Choice(m.Device, m.Index + 1, GameOn(m))).ToList();
            var picked = await Controls.MonitorPickWindow.Pick(title, hint, choices, def.Device, def.Screen.Bounds);
            return picked == null ? null : sel.Where(m => picked.Contains(m.Device)).ToList();
        }

        /// <summary>Resolution choices up to the selected monitor's own height (the smallest one when several are recorded).</summary>
        private void BuildResolutions()
        {
            var sel = SelectedMonitors.ToList();
            int native = sel.Count > 0 ? sel.Min(m => m.Screen.Bounds.Height) : 1080;
            var heights = Heights.Where(h => h == 0 || h < native).ToArray();
            if (!heights.Contains(settings.RecordHeight)) settings.RecordHeight = 0;
            ResolutionGrid.Columns = heights.Length;
            Segments(ResolutionGrid, heights, h => h == 0 ? (sel.Count > 1 ? "Как на экране" : $"Как на экране ({native}p)") : $"{h}p", settings.RecordHeight, v => settings.RecordHeight = v);
        }

        private RecordOptions BuildOptions(MonitorItem mon)
        {
            var b = mon.Screen.Bounds;
            int h = settings.RecordHeight >= b.Height ? 0 : settings.RecordHeight, w = 0;
            if (h > 0) w = (int)Math.Round(h * (double)b.Width / b.Height / 2) * 2;
            return new RecordOptions
            {
                Monitor = mon.Handle,
                Width = w, Height = h,
                Fps = settings.RecordFps, Quality = settings.RecordQuality,
                SystemSound = settings.RecordSystemSound, Microphone = settings.RecordMicrophone,
                ReplaySeconds = settings.ReplaySeconds, Folder = RecFolder,
            };
        }

        private void RecSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized) return;
            settings.RecordSystemSound = RecSystemSwitch.IsChecked == true;
            settings.RecordMicrophone = RecMicSwitch.IsChecked == true;
            RecSettingsChanged();
        }

        /// <summary>New settings apply to the replay buffer right away (a running recording keeps its own).</summary>
        private void RecSettingsChanged()
        {
            ScheduleSave();
            if (AnyBuffer) _ = StartReplayBuffer();
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
            if (ReplayService.Detected == ReplayService.Encoder.None)
            {
                RecStatus.Text = "Проверяю видеокарту…";
                await ReplayService.DetectEncoderAsync();
            }
            return true;
        }

        private async System.Threading.Tasks.Task StartReplayBuffer()
        {
            if (!await EnsureRecorderReady()) { ReplaySwitch.IsChecked = settings.ReplayEnabled = false; return; }
            var sel = SelectedMonitors.ToList();
            foreach (var kv in svcs.ToList())
                if (sel.All(m => m.Device != kv.Key)) kv.Value.StopBuffer();   // monitor was unticked
            foreach (var m in sel)
            {
                try { Svc(m).StartBuffer(BuildOptions(m)); }
                catch (Exception ex) { SettingsStore.LogError(ex); RecStatus.Text = "Не удалось запустить повторы: " + ex.Message; }
            }
            UpdateRecStatus();
        }

        private async void ReplaySwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.ReplayEnabled = ReplaySwitch.IsChecked == true;
            ScheduleSave();
            if (settings.ReplayEnabled) await StartReplayBuffer();
            else foreach (var s in svcs.Values) s.StopBuffer();
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
        private readonly Dictionary<string, string> startGame = new();                    // monitor → program at start
        private readonly Dictionary<string, Dictionary<string, int>> recSeconds = new();  // monitor → program → seconds

        /// <summary>Friendly program name of a process; null for the shell/desktop and for IO Companion itself.</summary>
        private string? AppName(uint pid)
        {
            if (pid == 0 || pid == Environment.ProcessId) return null;
            if (appNames.TryGetValue(pid, out var cached)) return cached;
            string? name = null;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (!ShellApps.Any(s => s.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase)))
                {
                    string? d = null;
                    try { d = p.MainModule?.FileVersionInfo.FileDescription?.Trim(); } catch { } // protected / elevated games
                    if (d != null && d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) d = d[..^4];
                    name = string.IsNullOrWhiteSpace(d) || GenericNames.Contains(d.ToLowerInvariant()) ? p.ProcessName : d;
                }
            }
            catch { }
            if (appNames.Count > 64) appNames.Clear();
            appNames[pid] = name;
            return name;
        }

        /// <summary>
        /// The program shown on one monitor: the active window if it is on that monitor, otherwise the
        /// top-most normal window there. So Discord in front on monitor 2 does not name a game recorded on monitor 1.
        /// </summary>
        private string GameOn(MonitorItem m)
        {
            try
            {
                var fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && MonitorFromWindow(fg, 2) == m.Handle && IsAppWindow(fg))
                {
                    GetWindowThreadProcessId(fg, out uint fpid);
                    var n = AppName(fpid);
                    if (n != null) return n;
                }
                string? found = null;
                EnumWindows((h, _) =>   // top of the z-order first
                {
                    if (!IsAppWindow(h) || MonitorFromWindow(h, 2) != m.Handle) return true;
                    GetWindowThreadProcessId(h, out uint pid);
                    found = AppName(pid);
                    return found == null;   // skip shell / own windows, keep looking
                }, IntPtr.Zero);
                return found ?? Desktop;
            }
            catch { return Desktop; }
        }

        /// <summary>A visible, not minimised, not hidden (cloaked) window of normal size; overlays and tool windows are skipped.</summary>
        private static bool IsAppWindow(IntPtr h)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return false;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return false;
            int ex = GetWindowLongStyle(h, -20 /* GWL_EXSTYLE */);
            if ((ex & 0x80 /* TOOLWINDOW */) != 0 || (ex & 0x20 /* TRANSPARENT */) != 0 || (ex & 0x08000000 /* NOACTIVATE */) != 0) return false;
            if (!GetWindowRect(h, out var r)) return false;
            return r.R - r.L >= 300 && r.B - r.T >= 200;
        }

        /// <summary>Once a second while recording: count how long each program was shown on each recorded monitor.</summary>
        private void TrackForeground()
        {
            foreach (var m in monitors)
            {
                if (!svcs.TryGetValue(m.Device, out var s) || !s.Recording) continue;
                if (!recSeconds.TryGetValue(m.Device, out var counts)) recSeconds[m.Device] = counts = new();
                string name = GameOn(m);
                if (name != Desktop) counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }

        /// <summary>
        /// Alt-Tab does not interrupt a recording (it captures the whole monitor) and never splits the file.
        /// The folder is the program the recording was started in on that monitor; if it was started on the
        /// desktop, the program that was shown there the longest.
        /// </summary>
        private string GameForRecording(string device)
        {
            string start = startGame.GetValueOrDefault(device, Desktop);
            if (start != Desktop || !recSeconds.TryGetValue(device, out var c) || c.Count == 0) return start;
            return c.MaxBy(kv => kv.Value).Key;
        }

        private bool shotBusy;

        /// <summary>One PNG per selected monitor, each in the folder of the program shown on it.</summary>
        public async System.Threading.Tasks.Task TakeScreenshot()
        {
            if (shotBusy) return;
            shotBusy = true;
            try
            {
                // shoot first (so the picker itself is not in the picture), then keep only the chosen ones
                var shots = new List<(MonitorItem m, string file)>();
                foreach (var m in SelectedMonitors.ToList())
                    shots.Add((m, await Svc(m).ScreenshotAsync(BuildOptions(m), GameOn(m), m.Screen.Bounds)));
                var keep = shots.Count > 1 ? await ChooseMonitors("Какой скриншот сохранить?", "Снимки уже сделаны, лишние удалятся.") : shots.Select(s => s.m).ToList();
                var saved = new List<string>();
                foreach (var (m, file) in shots)
                {
                    if (keep?.Contains(m) == true) { saved.Add(file); continue; }
                    try
                    {
                        File.Delete(file);
                        // remove Игра, Фото and Монитор N folders if they are left empty
                        var dir = Path.GetDirectoryName(file);
                        for (int up = 0; up < 3 && dir != null && !Directory.EnumerateFileSystemEntries(dir).Any(); up++)
                        { Directory.Delete(dir); dir = Path.GetDirectoryName(dir); }
                    }
                    catch { }
                }
                if (saved.Count == 0) return;
                NotifyChange(saved.Count > 1 ? $"Скриншоты сохранены ({saved.Count})" : "Скриншот сохранён", "",
                             string.Join(", ", saved.Select(Path.GetFileName)));
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
            if (!AnyBuffer)
            {
                NotifyChange("Повторы выключены", "", "Включите их на вкладке «Запись»");
                return;
            }
            recBusy = true;
            try
            {
                var buffered = monitors.Where(m => svcs.TryGetValue(m.Device, out var s) && s.BufferRunning).ToList();
                var chosen = buffered.Count > 1
                    ? await ChooseMonitors("Какой повтор сохранить?", $"Последние {DurationText(settings.ReplaySeconds)} с выбранного монитора.")
                    : buffered;
                if (chosen == null) return;
                chosen = chosen.Where(buffered.Contains).ToList();
                SaveReplayText.Text = "Сохраняю…";
                var jobs = chosen.Select(m => svcs[m.Device].SaveReplayAsync(GameOn(m))).ToList();
                var files = await System.Threading.Tasks.Task.WhenAll(jobs);
                NotifyChange(files.Length > 1 ? $"Повторы сохранены ({files.Length})" : "Повтор сохранён", "",
                             string.Join(", ", files.Select(Path.GetFileName)));
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NotifyChange("Не удалось сохранить повтор", "", ex.Message);
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
                if (AnyRecording)
                {
                    RecordText.Text = "Сохраняю…";
                    var jobs = svcs.Where(kv => kv.Value.Recording)
                                   .Select(kv => kv.Value.StopRecordingAsync(GameForRecording(kv.Key))).ToList();
                    var files = (await System.Threading.Tasks.Task.WhenAll(jobs)).Where(f => f != null).ToList();
                    NotifyChange(files.Count > 1 ? $"Записи сохранены ({files.Count})" : "Запись сохранена", "",
                                 files.Count > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : null);
                }
                else
                {
                    if (!await EnsureRecorderReady()) return;
                    var chosen = await ChooseMonitors("Какой монитор записывать?", "Запись идёт, пока не нажмёте клавишу ещё раз. Alt-Tab её не прерывает.");
                    if (chosen == null || chosen.Count == 0) return;
                    startGame.Clear();
                    recSeconds.Clear();
                    var started = new List<MonitorItem>();
                    try
                    {
                        foreach (var m in chosen)
                        {
                            startGame[m.Device] = GameOn(m);
                            Svc(m).StartRecording(BuildOptions(m), startGame[m.Device]);
                            started.Add(m);
                        }
                    }
                    catch
                    {
                        // one monitor failed: do not leave the others recording on their own
                        foreach (var m in started) _ = svcs[m.Device].StopRecordingAsync(startGame[m.Device]);
                        throw;
                    }
                    NotifyChange("Запись началась", "", (started.Count > 1 ? $"мониторов: {started.Count} · " : "") + $"{recordKey} — остановить");
                }
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NotifyChange("Ошибка записи", "", ex.Message);
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

        public bool RecordingActive => AnyRecording;
        public bool ReplayActive => AnyBuffer;

        private void UpdateRecStatus()
        {
            if (RecStatus == null || recBusy && RecStatus.Text.StartsWith("Скачиваю")) return;
            var codec = ReplayService.Detected;
            string enc = codec == ReplayService.Encoder.None ? "" : " · кодирует " + ReplayService.EncoderName(codec);
            var rec = svcs.Values.Where(s => s.Recording).ToList();
            if (rec.Count > 0)
            {
                var t = DateTime.Now - rec.Min(s => s.RecordingSince);
                RecordText.Text = $"Остановить  {(int)t.TotalMinutes:00}:{t.Seconds:00}";
            }
            else if (!recBusy) RecordText.Text = "Начать запись";

            int buffers = svcs.Values.Count(s => s.BufferRunning);
            if (buffers > 0)
                RecStatus.Text = $"Повторы включены{(buffers > 1 ? $" на {buffers} мониторах" : "")}: держу последние {DurationText(settings.ReplaySeconds)}{enc}. " +
                                 $"Временный буфер на диске: {svcs.Values.Sum(s => s.BufferBytes()) / (1024 * 1024)} МБ.";
            else if (settings.ReplayEnabled && !FfmpegInstaller.Installed)
                RecStatus.Text = "Включите переключатель — программа один раз скачает движок записи.";
            else if (!recBusy)
                RecStatus.Text = "Повторы выключены. Включите — и любой момент последних секунд можно сохранить одной клавишей." +
                                 (enc.Length > 0 ? enc : "");
            SaveReplayButton.IsEnabled = buffers > 0 && !recBusy;

            RecInfo.Text = (FfmpegInstaller.Installed ? "" : "При первом включении скачается FFmpeg (~110 МБ) — открытый движок записи, как в OBS. ") +
                           "Видеокарта сама сжимает видео (NVIDIA / AMD / Intel), поэтому игра почти не проседает. " +
                           "Защищённое видео (Netflix, Кинопоиск) Windows записывает чёрным экраном.";
        }

        private static string DurationText(int s) => s < 60 ? $"{s} секунд" : s == 60 ? "1 минуту" : s < 300 ? $"{s / 60} минуты" : $"{s / 60} минут";
    }
}
