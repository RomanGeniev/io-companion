using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GraphiteCompanion.Services;
using Microsoft.Win32;

namespace GraphiteCompanion
{
    // Replays (last N seconds by a hotkey) and manual screen recording.
    public partial class MainWindow
    {
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
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr bounds, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public WinRect Bounds, WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo { public uint Size, Time; }

        private sealed record MonitorItem(string Device, IntPtr Handle, System.Drawing.Rectangle Bounds, bool Primary,
            int Index, string Details)
        {
            public string Label => $"Монитор {Index + 1}";
        }

        private sealed record Option(int Value, string Label);

        private List<MonitorItem> monitors = new();
        private readonly Dictionary<string, ReplayService> svcs = new();   // one recorder per monitor
        private readonly Dictionary<string, ComboBox[]> monitorCombos = new();
        private IEnumerable<MonitorItem> SelectedMonitors => monitors.Where(m => settings.RecordMonitors?.Contains(m.Device) == true);
        private IEnumerable<MonitorItem> ReplayableMonitors => monitors.Where(m => svcs.TryGetValue(m.Device, out var s) && s.CanSaveReplay);

        private ReplayService Svc(MonitorItem m)
        {
            if (!svcs.TryGetValue(m.Device, out var s)) svcs[m.Device] = s = new ReplayService(m.Device.TrimStart('\\', '.'));
            s.MonitorName = $"Монитор {m.Index + 1}";
            return s;
        }

        private readonly Dictionary<string, RecordOptions> manualCaptures = new();
        private readonly Dictionary<string, long> nextReplayAttempt = new(), nextRecordAttempt = new();
        private DateTime manualStartedAt;
        private bool AnyRecording => manualCaptures.Count > 0;
        private DispatcherTimer? recTimer;
        private bool recBusy, startingReplay, recordClosed;
        private readonly List<Task> recordingSaves = new();
        private List<MonitorItem>? pendingMonitors;
        private long displayChangeAt, handledDisplayChange;
        private const long CaptureRetryMs = 15000;
        private string replayKey = "Alt + F10", recordKey = "Alt + F9", shotKey = "Alt + F8";

        private static readonly int[] Durations = { 15, 30, 60, 120, 300, 600, 900, 1800 };
        private static readonly int[] Heights = { 0, 2160, 1440, 1080, 720 };
        private static readonly int[] Fpss = { 30, 60, 120 };
        private static readonly string[] QualityNames = { "Низкое", "Среднее", "Высокое" };

        private const string TipDuration =
            "Сколько последних секунд программа постоянно держит наготове. По клавише повтора они сохраняются в ролик — " +
            "удобно, когда интересный момент уже случился. Больше секунд — больше места на диске под временный буфер.";
        private const string TipSound =
            "Звук компьютера записывается с устройства вывода Windows по умолчанию. Голос — с микрофона Windows по умолчанию для связи. Можно включить оба. Выбор микрофона на вкладке настройки сам по себе не меняет устройство записи.";
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

        private const int CustomValue = -1, MinReplay = 5, MaxReplay = 3600;

        private static string Plural(int n, string one, string few, string many)
        {
            int a = n % 100, b = n % 10;
            return a is >= 11 and <= 14 ? many : b == 1 ? one : b is >= 2 and <= 4 ? few : many;
        }

        /// <summary>"15 секунд", "1 минута", "1 мин 30 с", "15 минут". <paramref name="accusative"/>: "последнюю 1 минуту".</summary>
        private static string DurationLabel(int s, bool accusative = false)
        {
            if (s < 60) return $"{s} {Plural(s, accusative ? "секунду" : "секунда", "секунды", "секунд")}";
            if (s % 60 != 0) return $"{s / 60} мин {s % 60} с";
            int m = s / 60;
            return $"{m} {Plural(m, accusative ? "минуту" : "минута", "минуты", "минут")}";
        }

        private void BuildDurationCombo()
        {
            var list = Durations.Select(d => new Option(d, DurationLabel(d))).ToList();
            if (!Durations.Contains(settings.ReplaySeconds))
                list.Add(new Option(settings.ReplaySeconds, "Своё: " + DurationLabel(settings.ReplaySeconds)));
            list.Add(new Option(CustomValue, "Своё время…"));
            DurationCombo.ItemsSource = list;
            DurationCombo.SelectedItem = list.First(o => o.Value == settings.ReplaySeconds);
            UpdateDurationHint();
        }

        private void CustomDuration_Click(object sender, RoutedEventArgs e) => ApplyCustomDuration();

        private void CustomDurationBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) { ApplyCustomDuration(); e.Handled = true; }
        }

        private void ApplyCustomDuration()
        {
            if (!int.TryParse(CustomDurationBox.Text.Trim(), out int n) || n <= 0)
            {
                DurationHint.Text = "Введите целое число, например 15.";
                return;
            }
            long s = CustomDurationUnit.SelectedIndex == 0 ? n * 60L : n;
            if (s < MinReplay || s > MaxReplay)
            {
                DurationHint.Text = "Можно от 5 секунд до 60 минут.";
                return;
            }
            settings.ReplaySeconds = (int)s;
            CustomDurationPanel.Visibility = Visibility.Collapsed;
            BuildDurationCombo();
            RecSettingsChanged();
        }

        /// <summary>Rough bitrate of one monitor with its settings (hardware H.264), Mbit/s.</summary>
        private double EstimateMbps(MonitorItem m)
        {
            var r = Rec(m);
            var b = m.Bounds;
            double h = r.Height > 0 && r.Height < b.Height ? r.Height : b.Height;
            double w = h * b.Width / b.Height;
            double[] qf = { 0.6, 1.0, 1.6 };
            return 15.0 * (w * h) / (1920 * 1080) * r.Fps / 60.0 * qf[r.Quality];
        }

        /// <summary>How much disk the replay buffer takes for the chosen time on the selected monitors.</summary>
        private void UpdateDurationHint()
        {
            if (DurationHint == null) return;
            double mb = SelectedMonitors.Sum(m => EstimateMbps(m)) * settings.ReplaySeconds / 8.0;
            string size = mb >= 1024 ? $"{mb / 1024:0.#} ГБ" : $"{Math.Max(1, mb):0} МБ";
            string text = $"Временный буфер займёт примерно {size} на диске C:" +
                          (SelectedMonitors.Count() > 1 ? $" (на все мониторы)." : ".");
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!);
                double free = drive.AvailableFreeSpace / 1048576.0;
                // the saved copy needs the same space again while it is being joined
                if (mb * 2 > free) text += $" Свободно только {free / 1024:0.#} ГБ — уменьшите время или качество.";
            }
            catch { }
            DurationHint.Text = text;
        }

        /// <summary>Own resolution / fps / quality of a monitor; new monitors start from the old common settings.</summary>
        private MonitorRecordSettings Rec(MonitorItem m)
        {
            settings.RecordPerMonitor ??= new();
            if (!settings.RecordPerMonitor.TryGetValue(m.Device, out var r))
                settings.RecordPerMonitor[m.Device] = r = new MonitorRecordSettings { Height = settings.RecordHeight, Fps = settings.RecordFps, Quality = settings.RecordQuality };
            if (!Fpss.Contains(r.Fps)) r.Fps = 60;
            r.Quality = Math.Clamp(r.Quality, 0, 2);
            return r;
        }

        private void InitRecord()
        {
            BuildDurationCombo();
            DurationCombo.SelectionChanged += (_, _) =>
            {
                if (DurationCombo.SelectedItem is not Option o) return;
                if (o.Value == CustomValue)
                {
                    CustomDurationPanel.Visibility = Visibility.Visible;
                    bool minutes = settings.ReplaySeconds % 60 == 0;
                    CustomDurationUnit.SelectedIndex = minutes ? 0 : 1;
                    CustomDurationBox.Text = (minutes ? settings.ReplaySeconds / 60 : settings.ReplaySeconds).ToString();
                    CustomDurationBox.Focus();
                    CustomDurationBox.SelectAll();
                    return;
                }
                CustomDurationPanel.Visibility = Visibility.Collapsed;
                if (o.Value == settings.ReplaySeconds) return;
                settings.ReplaySeconds = o.Value;
                UpdateDurationHint();
                RecSettingsChanged();
            };
            DurationTitle.Children.Add(Info(TipDuration + " Можно задать своё время — от 5 секунд до 60 минут."));
            SoundTitle.Children.Add(Info(TipSound));
            MonitorsTitle.Children.Add(Info(TipMonitors));

            monitors = ReadMonitors() ?? new();
            // older versions stored one monitor; now it is a list (at least one is always selected)
            var sel = (settings.RecordMonitors ?? (settings.RecordMonitor != null ? new List<string> { settings.RecordMonitor } : new()))
                .Distinct().ToList();
            settings.RecordMonitors = sel;
            ApplyMonitorList(monitors);

            RecSystemSwitch.IsChecked = settings.RecordSystemSound;
            RecMicSwitch.IsChecked = settings.RecordMicrophone;
            ReplaySwitch.IsChecked = settings.ReplayEnabled;
            RecFolderText.Text = RecFolder;
            _ = OrganizeRecordingFolderAsync(RecFolder);

            UpdateHotkeyTexts();

            recTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            recTimer.Tick += (_, _) =>
            {
                try { RecoverCaptures(); }
                catch (Exception ex) { SettingsStore.LogError(ex); }
                TrackForeground();
                UpdateRecStatus();
            };
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            recTimer.Start();

            if (settings.ReplayEnabled && FfmpegInstaller.Installed) _ = StartReplayBuffer();
            UpdateRecStatus();
        }

        private static List<MonitorItem>? ReadMonitors()
        {
            var screens = new List<(string Device, IntPtr Handle, System.Drawing.Rectangle Bounds, bool Primary)>();
            bool ok = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(handle, ref info)) return false; // topology is still changing
                var b = info.Bounds;
                if (b.R > b.L && b.B > b.T)
                    screens.Add((info.Device, handle, System.Drawing.Rectangle.FromLTRB(b.L, b.T, b.R, b.B), (info.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);
            return !ok ? null : screens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top)
                .ThenBy(s => s.Device, StringComparer.Ordinal).Select((s, index) =>
                    new MonitorItem(s.Device, s.Handle, s.Bounds, s.Primary, index,
                        $"{s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " · основной" : "") + Position(index, screens.Count))).ToList();
        }

        private void ApplyMonitorList(List<MonitorItem> current)
        {
            monitors = current;
            if (settings.RecordMonitors!.Count == 0 && current.Count > 0)
                settings.RecordMonitors.Add((current.FirstOrDefault(m => m.Primary) ?? current[0]).Device);
            BuildMonitorRows();
            UpdateDurationHint();
            UpdateMonitorHint();
        }

        private void DisplaySettingsChanged(object? sender, EventArgs e) =>
            Interlocked.Exchange(ref displayChangeAt, Environment.TickCount64);

        private Task<T> TrackRecordingSave<T>(Task<T> operation)
        {
            recordingSaves.RemoveAll(task => task.IsCompleted);
            recordingSaves.Add(operation);
            return operation;
        }

        private async Task OrganizeRecordingFolderAsync(string root)
        {
            try { await TrackRecordingSave(ReplayService.OrganizeLegacyFoldersAsync(root)); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        private async Task DisposeRecordAsync()
        {
            recordClosed = true;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            recTimer?.Stop();
            manualCaptures.Clear();
            foreach (var (device, service) in svcs)
                if (service.HasRecordingSession)
                    _ = TrackRecordingSave(service.StopRecordingAsync(GameForRecording(device)));
            var disposing = svcs.Values.Select(service => service.DisposeAsync().AsTask()).ToArray();
            try { await Task.WhenAll(disposing); }
            finally { await Task.WhenAll(recordingSaves.ToArray()); }
        }

        private static bool SameCaptureDisplay(MonitorItem? old, MonitorItem current) =>
            old != null && old.Handle == current.Handle && old.Bounds.Size == current.Bounds.Size;

        private void RecoverCaptures()
        {
            if (recordClosed || startingReplay) return;
            var current = ReadMonitors();
            if (current == null) return;
            long now = Environment.TickCount64;
            long signal = Interlocked.Read(ref displayChangeAt);
            bool displayChanged = signal != handledDisplayChange;
            bool topologyChanged = !monitors.SequenceEqual(current);
            if (topologyChanged && (pendingMonitors == null || !pendingMonitors.SequenceEqual(current)))
            {
                pendingMonitors = current;
                return;
            }
            if (displayChanged && now - signal < 1800) return;
            pendingMonitors = null;
            handledDisplayChange = signal;

            var before = monitors;
            if (topologyChanged) ApplyMonitorList(current);
            // Some driver resets keep both the old size and HMONITOR. In that case Windows
            // gives us no changed monitor to isolate, so rebind the active captures.
            bool rebindAll = displayChanged && !topologyChanged;
            foreach (var (device, service) in svcs)
            {
                var monitor = current.Find(m => m.Device == device);
                if (monitor != null && (rebindAll || !SameCaptureDisplay(before.Find(m => m.Device == device), monitor)))
                {
                    nextReplayAttempt.Remove(device);
                    nextRecordAttempt.Remove(device);
                }
                if (service.HasBufferSession && (monitor == null || rebindAll ||
                    !service.BufferMatches(monitor.Handle, monitor.Bounds.Size) ||
                    !service.BufferRunning || service.BufferStalled(now, false)))
                    service.StopBuffer(preserve: true);
                if (service.HasRecordingSession && manualCaptures.TryGetValue(device, out var active) &&
                    (monitor == null || rebindAll || active.Monitor != monitor.Handle || active.MonitorBounds.Size != monitor.Bounds.Size ||
                     !service.Recording || service.RecordingStalled(now, false)))
                    _ = SaveInterruptedRecording(service, GameForRecording(device));
            }
            if (settings.ReplayEnabled && FfmpegInstaller.Installed && ReplayService.Detected != ReplayService.Encoder.None)
                foreach (var m in SelectedMonitors)
                {
                    var service = Svc(m);
                    if (!service.BufferRunning && now >= nextReplayAttempt.GetValueOrDefault(m.Device))
                        StartMonitorBuffer(m, preserve: true);
                    service.PrunePreviousBuffer();
                }
            foreach (var m in current)
                if (manualCaptures.TryGetValue(m.Device, out var options) && !Svc(m).Recording && !Svc(m).RecordingStopping &&
                    now >= nextRecordAttempt.GetValueOrDefault(m.Device))
                {
                    try { StartManualCapture(m, options with { Monitor = m.Handle, MonitorBounds = m.Bounds }); }
                    catch (Exception ex) { SettingsStore.LogError(ex); }
                }
        }

        private async Task SaveInterruptedRecording(ReplayService service, string game)
        {
            try
            {
                string? file = await TrackRecordingSave(service.StopRecordingAsync(game, interrupted: true));
                if (file != null && !recordClosed)
                    NotifyChange("Сохранена часть записи до сбоя захвата", "", Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                if (!recordClosed) NotifyChange("Не удалось собрать часть записи", "", ex.Message);
            }
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
                Controls.HelpTips.Set(sw, "Включите, чтобы записывать этот монитор. Каждый выбранный экран получает свой буфер и отдельный файл. Номер можно проверить кнопкой «Показать номера».");
                Put(row, sw, 0);

                int native = m.Bounds.Height;
                var res = Combo(Heights.Where(h => h == 0 || h < native).Select(h => new Option(h, h == 0 ? $"Как на экране ({native}p)" : $"{h}p")), r.Height, v => r.Height = v);
                var fps = Combo(Fpss.Select(f => new Option(f, $"{f}")), r.Fps, v => r.Fps = v);
                var q = Combo(Enumerable.Range(0, 3).Select(i => new Option(i, QualityNames[i])), r.Quality, v => r.Quality = v);
                Controls.HelpTips.Set(res, TipResolution);
                Controls.HelpTips.Set(fps, TipFps);
                Controls.HelpTips.Set(q, TipQuality);
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
                "Файлы сохраняются в «Монитор N\\Игра». В названии указаны дата и тип — запись, повтор или скриншот. Игра определяется по окну на этом мониторе.";
        }

        private void IdentifyMonitors_Click(object sender, RoutedEventArgs e) =>
            Controls.MonitorNumbers.Show(monitors.Select(m => (m.Bounds, m.Index + 1, m.Details.TrimStart(' ', '·'))).ToList(), TimeSpan.FromSeconds(3), centre: true);

        /// <summary>
        /// Which of the selected monitors to save. One selected → that one, no question.
        /// Several → a picker over the game, pre-highlighting the monitor with the active window,
        /// with each monitor's number shown in its corner meanwhile. Null = cancelled.
        /// </summary>
        private async Task<List<MonitorItem>?> ChooseMonitors(string title, string hint, List<MonitorItem>? available = null)
        {
            var sel = available ?? SelectedMonitors.ToList();
            if (sel.Count <= 1) return sel;
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out uint pid);
            var fgMon = fg != IntPtr.Zero && pid != Environment.ProcessId ? MonitorFromWindow(fg, 2) : IntPtr.Zero;
            var def = sel.FirstOrDefault(m => m.Handle == fgMon) ?? sel.FirstOrDefault(m => m.Primary) ?? sel[0];
            var choices = sel.Select(m => new Controls.MonitorPickWindow.Choice(m.Device, m.Index + 1, GameOn(m) + Position(m.Index, monitors.Count))).ToList();
            using var badges = Controls.MonitorNumbers.Show(sel.Select(m => (m.Bounds, m.Index + 1, "")).ToList(), null, centre: false);
            var picked = await Controls.MonitorPickWindow.Pick(title, hint, choices, def.Device, def.Bounds);
            return picked == null ? null : sel.Where(m => picked.Contains(m.Device)).ToList();
        }

        private RecordOptions BuildOptions(MonitorItem mon)
        {
            var r = Rec(mon);
            return new RecordOptions
            {
                Monitor = mon.Handle, MonitorBounds = mon.Bounds, RequestedHeight = r.Height,
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
            UpdateDurationHint();
            if (settings.ReplayEnabled && FfmpegInstaller.Installed) _ = StartReplayBuffer();
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

        private async Task StartReplayBuffer()
        {
            if (startingReplay || recordClosed) return;
            startingReplay = true;
            try
            {
                if (!await EnsureRecorderReady())
                {
                    if (!recordClosed) ReplaySwitch.IsChecked = settings.ReplayEnabled = false;
                    return;
                }
                if (recordClosed || !settings.ReplayEnabled) return;
                var sel = SelectedMonitors.ToList();
                foreach (var kv in svcs)
                    if (settings.RecordMonitors?.Contains(kv.Key) != true) kv.Value.StopBuffer();   // monitor was unticked
                foreach (var m in sel) StartMonitorBuffer(m);
            }
            finally { startingReplay = false; if (!recordClosed) UpdateRecStatus(); }
        }

        private void StartMonitorBuffer(MonitorItem monitor, bool preserve = false)
        {
            nextReplayAttempt[monitor.Device] = Environment.TickCount64 + CaptureRetryMs;
            try { Svc(monitor).StartBuffer(BuildOptions(monitor), preserve); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        private async void ReplaySwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.ReplayEnabled = ReplaySwitch.IsChecked == true;
            ScheduleSave();
            if (settings.ReplayEnabled) await StartReplayBuffer();
            else
            {
                nextReplayAttempt.Clear();
                foreach (var s in svcs.Values) s.StopBuffer();
            }
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

        /// <summary>Friendly program name of a process; null for the shell/desktop and for SoundShot itself.</summary>
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
            if (shotBusy || recordClosed) return;
            shotBusy = true;
            try
            {
                // shoot first (so the picker itself is not in the picture), then keep only the chosen ones
                var shots = new List<(MonitorItem m, string file)>();
                var current = ReadMonitors();
                var selected = current?.Where(m => settings.RecordMonitors?.Contains(m.Device) == true).ToList();
                if (selected == null || selected.Count == 0)
                {
                    NotifyChange("Монитор недоступен", "", "Дождитесь появления изображения и повторите снимок");
                    return;
                }
                foreach (var m in selected)
                {
                    if (recordClosed) break;
                    shots.Add((m, await TrackRecordingSave(Svc(m).ScreenshotAsync(BuildOptions(m), GameOn(m), m.Bounds))));
                }
                var keep = shots.Count > 1 && !recordClosed ? await ChooseMonitors("Какой скриншот сохранить?", "Снимки уже сделаны, лишние удалятся.", selected) : selected;
                var saved = new List<string>();
                foreach (var (m, file) in shots)
                {
                    if (keep?.Contains(m) == true) { saved.Add(file); continue; }
                    try
                    {
                        File.Delete(file);
                        // Remove only the empty game and monitor folders, keeping the chosen root.
                        var dir = Path.GetDirectoryName(file);
                        for (int up = 0; up < 2 && dir != null && !Directory.EnumerateFileSystemEntries(dir).Any(); up++)
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
            if (recBusy || recordClosed) return;
            var buffered = ReplayableMonitors.ToList();
            if (buffered.Count == 0)
            {
                if (settings.ReplayEnabled)
                    NotifyChange("Повтор ещё не готов", "", "Подождите, пока появятся кадры захвата");
                else NotifyChange("Повторы выключены", "", "Включите их на вкладке «Запись»");
                return;
            }
            recBusy = true;
            try
            {
                var chosen = buffered.Count > 1
                    ? await ChooseMonitors("Какой повтор сохранить?", $"Последние {DurationText(settings.ReplaySeconds)} с выбранного монитора.", buffered)
                    : buffered;
                if (chosen == null || recordClosed) return;
                SaveReplayText.Text = "Сохраняю…";
                var jobs = chosen.Select(m => svcs[m.Device].SaveReplayAsync(GameOn(m))).ToList();
                var files = await TrackRecordingSave(Task.WhenAll(jobs));
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

        private void StartManualCapture(MonitorItem monitor, RecordOptions options)
        {
            string device = monitor.Device;
            nextRecordAttempt[device] = Environment.TickCount64 + CaptureRetryMs;
            string game = GameOn(monitor);
            Svc(monitor).StartRecording(options, game);
            manualCaptures[device] = options;
            startGame[device] = game;
            recSeconds.Remove(device);
        }

        public async System.Threading.Tasks.Task ToggleRecording()
        {
            if (recBusy || recordClosed) return;
            recBusy = true;
            try
            {
                if (AnyRecording)
                {
                    RecordText.Text = "Сохраняю…";
                    manualCaptures.Clear();
                    nextRecordAttempt.Clear();
                    var jobs = svcs.Where(kv => kv.Value.HasRecordingSession)
                                   .Select(kv => kv.Value.StopRecordingAsync(GameForRecording(kv.Key))).ToList();
                    var files = (await TrackRecordingSave(Task.WhenAll(jobs))).Where(f => f != null).ToList();
                    NotifyChange(files.Count == 0 ? "Запись остановлена" : files.Count > 1 ? $"Записи сохранены ({files.Count})" : "Запись сохранена",
                                 "", files.Count > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : null);
                }
                else
                {
                    if (!await EnsureRecorderReady() || recordClosed) return;
                    var chosen = await ChooseMonitors("Какой монитор записывать?", "Запись идёт, пока не нажмёте клавишу ещё раз. Alt-Tab её не прерывает.");
                    if (chosen == null || recordClosed) return;
                    var latest = ReadMonitors();
                    chosen = latest?.Where(m => chosen.Any(previous => previous.Device == m.Device)).ToList();
                    if (chosen == null || chosen.Count == 0)
                    {
                        NotifyChange("Монитор недоступен", "", "Дождитесь появления изображения и начните запись снова");
                        return;
                    }
                    startGame.Clear();
                    recSeconds.Clear();
                    var started = new List<MonitorItem>();
                    try
                    {
                        foreach (var m in chosen)
                        {
                            StartManualCapture(m, BuildOptions(m));
                            started.Add(m);
                        }
                    }
                    catch
                    {
                        // one monitor failed: do not leave the others recording on their own
                        manualCaptures.Clear();
                        foreach (var m in started) _ = SaveInterruptedRecording(svcs[m.Device], startGame[m.Device]);
                        throw;
                    }
                    manualStartedAt = DateTime.Now;
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
            _ = OrganizeRecordingFolderAsync(RecFolder);
            RecSettingsChanged();
        }

        public bool RecordingActive => AnyRecording;
        public bool ReplayActive => ReplayableMonitors.Any();

        private void UpdateRecStatus()
        {
            if (RecStatus == null || !PageRecord.IsVisible || recBusy && RecStatus.Text.StartsWith("Скачиваю")) return;
            var codec = ReplayService.Detected;
            string enc = codec == ReplayService.Encoder.None ? "" : " · кодирует " + ReplayService.EncoderName(codec);
            if (AnyRecording)
            {
                var t = DateTime.Now - manualStartedAt;
                bool restoring = manualCaptures.Keys.Any(device => !svcs[device].Recording);
                RecordText.Text = restoring ? "Остановить · восстановление записи" : $"Остановить  {(int)t.TotalMinutes:00}:{t.Seconds:00}";
            }
            else if (!recBusy) RecordText.Text = "Начать запись";

            int buffers = SelectedMonitors.Count(m => svcs.TryGetValue(m.Device, out var s) && s.BufferReceivingFrames);
            int expected = settings.RecordMonitors?.Count ?? 0;
            bool replayReady = ReplayActive;
            if (settings.ReplayEnabled && FfmpegInstaller.Installed && buffers > 0 && buffers == expected)
                RecStatus.Text = $"Повторы включены · лимит {DurationText(settings.ReplaySeconds)}{enc}. " +
                                 string.Join("; ", SelectedMonitors.Where(m => svcs.ContainsKey(m.Device)).Select(m =>
                                 { var s = svcs[m.Device]; int seconds = s.BufferedSeconds;
                                   return $"{s.MonitorName}: накоплено {seconds / 60}:{seconds % 60:00}"; })) + ". " +
                                 $"Временный буфер на диске: {svcs.Values.Sum(s => s.BufferBytes()) / (1024 * 1024)} МБ.";
            else if (settings.ReplayEnabled && FfmpegInstaller.Installed)
            {
                RecStatus.Text = !SelectedMonitors.Any() ? "Жду подключения выбранного монитора." :
                    $"Жду новые кадры{(buffers > 0 ? $" · работают {buffers} из {expected}" : "")}.";
                if (replayReady) RecStatus.Text += " Можно сохранить последний записанный фрагмент.";
            }
            else if (settings.ReplayEnabled && !FfmpegInstaller.Installed)
                RecStatus.Text = "Включите переключатель — программа один раз скачает движок записи.";
            else if (!recBusy)
                RecStatus.Text = "Повторы выключены. Включите — и любой момент последних секунд можно сохранить одной клавишей." +
                                 (enc.Length > 0 ? enc : "");
            SaveReplayButton.IsEnabled = replayReady && !recBusy;

            RecInfo.Text = (FfmpegInstaller.Installed ? "" : "При первом включении скачается FFmpeg (~110 МБ) — открытый движок записи, как в OBS. ") +
                           "Видеокарта сама сжимает видео (NVIDIA / AMD / Intel), поэтому игра почти не проседает. " +
                           "Защищённое видео (Netflix, Кинопоиск) Windows записывает чёрным экраном.";
        }

        private static string DurationText(int s) => DurationLabel(s, accusative: true);
    }
}
