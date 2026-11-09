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
            public int Index;                  // 0-based; monitors are numbered left to right
            public string Label { get; init; } = "";
            public string Details = "";        // "2560×1440 · основной · слева"
            public WF.Screen Screen = null!;
            public IntPtr Handle;
        }

        private sealed record Option(int Value, string Label);

        private List<MonitorItem> monitors = new();
        private readonly Dictionary<string, ReplayService> svcs = new();   // one recorder per monitor
        private readonly Dictionary<string, ComboBox[]> monitorCombos = new();
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
        private static readonly string[] QualityNames = { "Низкое", "Среднее", "Высокое" };

        private const string TipDuration =
            "Сколько последних секунд программа постоянно держит наготове. По клавише повтора они сохраняются в ролик — " +
            "удобно, когда интересный момент уже случился. Больше секунд — больше места на диске под временный буфер.";
        private const string TipSound =
            "Звук компьютера — всё, что вы слышите в наушниках (игра, Discord, музыка). Микрофон — ваш голос. Можно включить оба.";
        private const string TipMonitors =
            "Мониторы пронумерованы слева направо, как они стоят в настройках Windows. Нажмите «Показать номера» — " +
            "на каждом экране появится его номер. Включите мониторы, которые хотите записывать, и задайте каждому своё качество.";
        private const string TipResolution =
            "Разрешение — сколько точек в кадре (размер картинки). 1080p = 1920×1080. Меньше разрешение — мельче картинка, " +
            "меньше файл и нагрузка на видеокарту. «Как на экране» — без уменьшения.";
        private const string TipFps =
            "Кадров в секунду — плавность движения. 60 — обычный выбор для игр. 120 — очень плавно, подходит для быстрых игр " +
            "и замедленного просмотра, но файл больше. 30 — экономно, для спокойных программ.";
        private const string TipQuality =
            "Качество — насколько бережно сжимается каждый кадр при том же разрешении. Высокое — меньше «мыла» и квадратиков " +
            "в динамичных сценах, но файл крупнее. Разрешение задаёт размер картинки, а качество — её чёткость.";

        private string RecFolder =>
            settings.ReplayFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IO Companion");

        private static string DurationLabel(int d) => d < 60 ? $"{d} секунд" : d == 60 ? "1 минута" : d < 300 ? $"{d / 60} минуты" : $"{d / 60} минут";

        /// <summary>Own resolution / fps / quality of a monitor; new monitors start from the old common settings.</summary>
        private MonitorRecordSettings Rec(MonitorItem m)
        {
            settings.RecordPerMonitor ??= new();
            if (!settings.RecordPerMonitor.TryGetValue(m.Device, out var r))
                settings.RecordPerMonitor[m.Device] = r = new MonitorRecordSettings { Height = settings.RecordHeight, Fps = settings.RecordFps, Quality = settings.RecordQuality };
            if (r.Height >= m.Screen.Bounds.Height) r.Height = 0;
            if (!Fpss.Contains(r.Fps)) r.Fps = 60;
            r.Quality = Math.Clamp(r.Quality, 0, 2);
            return r;
        }

        private void InitRecord()
        {
            DurationCombo.ItemsSource = Durations.Select(d => new Option(d, DurationLabel(d))).ToList();
            DurationCombo.SelectedItem = ((List<Option>)DurationCombo.ItemsSource).FirstOrDefault(o => o.Value == settings.ReplaySeconds);
            DurationCombo.SelectionChanged += (_, _) =>
            {
                if (DurationCombo.SelectedItem is Option o) { settings.ReplaySeconds = o.Value; RecSettingsChanged(); }
            };
            DurationTitle.Children.Add(Info(TipDuration));
            SoundTitle.Children.Add(Info(TipSound));
            MonitorsTitle.Children.Add(Info(TipMonitors));

            var screens = WF.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top).ToList();
            monitors = screens.Select((s, i) => new MonitorItem
            {
                Device = s.DeviceName, Screen = s, Index = i,
                Label = $"Монитор {i + 1}",
                Details = $"{s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " · основной" : "") + Position(i, screens.Count),
                Handle = MonitorFromPoint(new System.Drawing.Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2), 2),
            }).ToList();
            // older versions stored one monitor; now it is a list (at least one is always selected)
            var sel = (settings.RecordMonitors ?? (settings.RecordMonitor != null ? new List<string> { settings.RecordMonitor } : new()))
                .Where(d => monitors.Any(m => m.Device == d)).Distinct().ToList();
            if (sel.Count == 0) sel.Add((monitors.FirstOrDefault(m => m.Screen.Primary) ?? monitors[0]).Device);
            settings.RecordMonitors = sel;
            BuildMonitorRows();
            UpdateMonitorHint();

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

        private static string Position(int i, int count) =>
            count < 2 ? "" : i == 0 ? " · слева" : i == count - 1 ? " · справа" : count == 3 ? " · по центру" : $" · {i + 1}-й слева";

        private string RegFirst(Action action, params (uint mods, uint key, string label)[] options)
        {
            foreach (var o in options)
                if (hotkeys.Register(o.mods, o.key, action)) return o.label;
            return "не назначено";
        }

        /// <summary>Small "i" next to a title: hint on hover (shows quickly) or on click / press-and-hold.</summary>
        private FrameworkElement Info(string text)
        {
            var icon = new TextBlock
            {
                Text = "", FontSize = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                Cursor = System.Windows.Input.Cursors.Help, Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont"),
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var tip = new ToolTip { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 } };
            icon.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(icon, 150);
            ToolTipService.SetShowDuration(icon, 60000);
            icon.MouseLeftButtonDown += (_, e) => { tip.PlacementTarget = icon; tip.IsOpen = true; e.Handled = true; };
            icon.MouseLeave += (_, _) => tip.IsOpen = false;
            return icon;
        }

        private static TextBlock Head(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        /// <summary>One row per monitor: switch (record it or not) + its own resolution, fps and quality.</summary>
        private void BuildMonitorRows()
        {
            MonitorPanel.Children.Clear();
            monitorCombos.Clear();

            Grid Row()
            {
                var g = new Grid();
                foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(10), new GridLength(210), new GridLength(10), new GridLength(84), new GridLength(10), new GridLength(116) })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                return g;
            }
            void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }
            StackPanel Titled(string t, string tip) { var p = new StackPanel { Orientation = Orientation.Horizontal }; var h = Head(t); h.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); p.Children.Add(h); p.Children.Add(Info(tip)); return p; }

            var header = Row();
            header.Margin = new Thickness(12, 0, 12, 6);
            Put(header, Titled("РАЗРЕШЕНИЕ", TipResolution), 2);
            Put(header, Titled("КАДРОВ/С", TipFps), 4);
            Put(header, Titled("КАЧЕСТВО", TipQuality), 6);
            MonitorPanel.Children.Add(header);

            foreach (var m in monitors)
            {
                var r = Rec(m);
                var row = Row();
                var title = new StackPanel();
                title.Children.Add(new TextBlock { Text = m.Label, FontWeight = FontWeights.SemiBold });
                var det = new TextBlock { Text = m.Details, FontSize = 12 };
                det.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                title.Children.Add(det);
                var sw = new CheckBox { Content = title, IsChecked = settings.RecordMonitors!.Contains(m.Device), Tag = m, VerticalAlignment = VerticalAlignment.Center };
                sw.SetResourceReference(StyleProperty, "Switch");
                sw.Click += MonitorCheck_Click;
                Put(row, sw, 0);

                int native = m.Screen.Bounds.Height;
                var res = Combo(Heights.Where(h => h == 0 || h < native).Select(h => new Option(h, h == 0 ? $"Как на экране ({native}p)" : $"{h}p")), r.Height, v => r.Height = v);
                var fps = Combo(Fpss.Select(f => new Option(f, $"{f}")), r.Fps, v => r.Fps = v);
                var q = Combo(Enumerable.Range(0, 3).Select(i => new Option(i, QualityNames[i])), r.Quality, v => r.Quality = v);
                Put(row, res, 2); Put(row, fps, 4); Put(row, q, 6);
                monitorCombos[m.Device] = new[] { res, fps, q };

                var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), Child = row };
                card.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
                MonitorPanel.Children.Add(card);
            }
            UpdateMonitorEnabled();
        }

        private ComboBox Combo(IEnumerable<Option> options, int current, Action<int> set)
        {
            var list = options.ToList();
            var cb = new ComboBox { ItemsSource = list, DisplayMemberPath = "Label", VerticalAlignment = VerticalAlignment.Center };
            cb.SelectedItem = list.FirstOrDefault(o => o.Value == current) ?? list[0];
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is Option o) { set(o.Value); RecSettingsChanged(); } };
            return cb;
        }

        private void UpdateMonitorEnabled()
        {
            foreach (var (dev, combos) in monitorCombos)
                foreach (var c in combos) c.IsEnabled = settings.RecordMonitors?.Contains(dev) == true;
        }

        private void MonitorCheck_Click(object sender, RoutedEventArgs e)
        {
            var cb = (CheckBox)sender;
            var switches = MonitorPanel.Children.OfType<Border>().Select(b => ((Grid)b.Child).Children.OfType<CheckBox>().First()).ToList();
            var list = switches.Where(c => c.IsChecked == true).Select(c => ((MonitorItem)c.Tag).Device).ToList();
            if (list.Count == 0) { cb.IsChecked = true; list.Add(((MonitorItem)cb.Tag).Device); } // can't record nothing
            settings.RecordMonitors = list;
            UpdateMonitorEnabled();
            UpdateMonitorHint();
            RecSettingsChanged();
        }

        private void UpdateMonitorHint()
        {
            MonitorHint.Text = (SelectedMonitors.Count() > 1
                ? "Включено несколько мониторов: по клавише записи, повтора или скриншота появится выбор, какой из них сохранить (или все). "
                : "") +
                "Файлы лягут в «Монитор N\\Видео\\Игра» и «Монитор N\\Фото\\Игра»; игра определяется по окну на этом мониторе.";
        }

        private void IdentifyMonitors_Click(object sender, RoutedEventArgs e) =>
            Controls.MonitorNumbers.Show(monitors.Select(m => (m.Screen.Bounds, m.Index + 1, m.Details.TrimStart(' ', '·'))).ToList(), TimeSpan.FromSeconds(3), centre: true);

        /// <summary>
        /// Which of the selected monitors to save. One selected → that one, no question.
        /// Several → a picker over the game, pre-highlighting the monitor with the active window,
        /// with each monitor's number shown in its corner meanwhile. Null = cancelled.
        /// </summary>
        private async System.Threading.Tasks.Task<List<MonitorItem>?> ChooseMonitors(string title, string hint)
        {
            var sel = SelectedMonitors.ToList();
            if (sel.Count <= 1) return sel;
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint pid);
            var fgMon = fg != IntPtr.Zero && pid != Environment.ProcessId ? MonitorFromWindow(fg, 2) : IntPtr.Zero;
            var def = sel.FirstOrDefault(m => m.Handle == fgMon) ?? sel.FirstOrDefault(m => m.Screen.Primary) ?? sel[0];
            var choices = sel.Select(m => new Controls.MonitorPickWindow.Choice(m.Device, m.Index + 1, GameOn(m) + Position(m.Index, monitors.Count))).ToList();
            using var badges = Controls.MonitorNumbers.Show(sel.Select(m => (m.Screen.Bounds, m.Index + 1, "")).ToList(), null, centre: false);
            var picked = await Controls.MonitorPickWindow.Pick(title, hint, choices, def.Device, def.Screen.Bounds);
            return picked == null ? null : sel.Where(m => picked.Contains(m.Device)).ToList();
        }

        private RecordOptions BuildOptions(MonitorItem mon)
        {
            var b = mon.Screen.Bounds;
            var r = Rec(mon);
            int h = r.Height >= b.Height ? 0 : r.Height, w = 0;
            if (h > 0) w = (int)Math.Round(h * (double)b.Width / b.Height / 2) * 2;
            return new RecordOptions
            {
                Monitor = mon.Handle,
                Width = w, Height = h,
                Fps = r.Fps, Quality = r.Quality,
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
