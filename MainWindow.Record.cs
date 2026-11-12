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
        private readonly Dictionary<string, Controls.MonitorRecordingCard> recordingCards = new();
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
        private readonly Dictionary<string, DateTime> manualStartTimes = new();
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
            "Звук компьютера записывается с устройства вывода Windows по умолчанию. Микрофон для откатов и записей выбирается здесь, отдельно от вкладки настройки микрофона. Можно включить оба источника.";
        private const string TipMonitors =
            "Мониторы пронумерованы слева направо, как они стоят в настройках Windows. Нажмите «Показать номера» — " +
            "на каждом экране появится его номер. Каждая карточка управляет одним экраном: записью, повторами, снимками и качеством.";
        private const string TipResolution =
            "Разрешение — сколько точек в кадре (размер картинки). 1080p = 1920×1080. Меньше разрешение — мельче картинка, " +
            "меньше файл и нагрузка на видеокарту. Максимальное значение — исходный размер экрана.";
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
            string text = $"Примерный размер буфера: {size}";
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
            RefreshRecordingMicrophoneChoices();
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
            if (settings.ReplayEnabled && settings.RecordMonitors!.Count == 0 && current.Count > 0)
                settings.RecordMonitors.Add((current.FirstOrDefault(m => m.Primary) ?? current[0]).Device);
            BuildMonitorRows();
            UpdateDurationHint();

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
            MonitorPanel.Children.Clear(); MonitorPanel.ColumnDefinitions.Clear(); MonitorPanel.RowDefinitions.Clear(); recordingCards.Clear();
            int columns = Math.Max(1, Math.Min(3, monitors.Count));
            for (int i = 0; i < columns; i++) MonitorPanel.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (monitors.Count + columns - 1) / columns; i++) MonitorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i]; var r = Rec(m);
                var card = new Controls.MonitorRecordingCard(m.Label, m.Details);
                IEnumerable<Controls.MonitorRecordingCard.Choice> Choices(IEnumerable<Option> list) => list.Select(o => new Controls.MonitorRecordingCard.Choice(o.Value, o.Label));
                card.AddQuality("Разрешение", Choices(Heights.Where(h => h == 0 || h < m.Bounds.Height).Select(h => new Option(h, h == 0 ? $"{m.Bounds.Height}p" : $"{h}p"))), r.Height, TipResolution, v => { r.Height = v; RecSettingsChanged(); });
                card.AddQuality("Кадров/с", Choices(Fpss.Select(v => new Option(v, v.ToString()))), r.Fps, TipFps, v => { r.Fps = v; RecSettingsChanged(); });
                card.AddQuality("Качество", Choices(Enumerable.Range(0, 3).Select(v => new Option(v, QualityNames[v]))), r.Quality, TipQuality, v => { r.Quality = v; RecSettingsChanged(); });
                card.RepeatRequested += async () => await ToggleMonitorReplay(m);
                card.SaveRequested += async () => await SaveReplayFor(m.Device);
                card.RecordRequested += async () => await ToggleRecordingFor(m.Device);
                card.ScreenshotRequested += async () => await TakeScreenshotFor(m.Device);
                card.Complete(); Grid.SetColumn(card, i % columns); Grid.SetRow(card, i / columns);
                MonitorPanel.Children.Add(card); recordingCards[m.Device] = card;
            }
            if (monitors.Count == 0) MonitorPanel.Children.Add(new TextBlock { Text = "Подключённые мониторы не найдены.", Margin = new Thickness(0, 12, 0, 12) });
            SyncMonitorLayout();
            UpdateRecStatus();
        }



        private async Task ToggleMonitorReplay(MonitorItem monitor)
        {
            if (recBusy || startingReplay || recordClosed) return;
            settings.RecordMonitors ??= new();
            if (settings.ReplayEnabled && settings.RecordMonitors.Contains(monitor.Device))
            {
                settings.RecordMonitors.Remove(monitor.Device);
                if (svcs.TryGetValue(monitor.Device, out var service)) service.StopBuffer();
                nextReplayAttempt.Remove(monitor.Device);
                settings.ReplayEnabled = settings.RecordMonitors.Count > 0;
            }
            else
            {
                if (!settings.ReplayEnabled) settings.RecordMonitors.Clear();
                if (!settings.RecordMonitors.Contains(monitor.Device)) settings.RecordMonitors.Add(monitor.Device);
                settings.ReplayEnabled = true;
                await StartReplayBuffer();
            }
            ScheduleSave(); UpdateDurationHint(); UpdateRecStatus();
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
            var sel = available ?? monitors.ToList();
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
                MicrophoneDeviceId = settings.RecordMicrophoneDeviceId,
                ReplaySeconds = settings.ReplaySeconds, Folder = RecFolder,
            };
        }

        private void RecSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized) return;
            settings.RecordSystemSound = RecSystemSwitch.IsChecked == true;
            settings.RecordMicrophone = RecMicSwitch.IsChecked == true;
            RefreshRecordingMicrophoneChoices();
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
                SetRecorderMessage("Скачиваю движок записи FFmpeg (один раз, ~110 МБ)…");
                try
                {
                    await FfmpegInstaller.InstallAsync(new Progress<double>(p => SetRecorderMessage($"Скачиваю движок записи FFmpeg… {p:P0}")));
                }
                catch (Exception ex)
                {
                    SettingsStore.LogError(ex);
                    SetRecorderMessage("Не удалось скачать FFmpeg: " + ex.Message);
                    return false;
                }
            }
            if (ReplayService.Detected == ReplayService.Encoder.None)
            {
                SetRecorderMessage("Проверяю видеокарту…");
                await ReplayService.DetectEncoderAsync();
            }
            SetRecorderMessage("");
            return true;
        }

        private async Task StartReplayBuffer()
        {
            if (startingReplay || recordClosed) return;
            startingReplay = true;
            UpdateRecStatus();
            try
            {
                if (!await EnsureRecorderReady())
                {
                    if (!recordClosed) settings.ReplayEnabled = false;
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
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                SetRecorderMessage($"Не удалось запустить повтор: {monitor.Label}. {ex.Message}");
            }
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
        public Task TakeScreenshot() => TakeScreenshotFor(null);

        private async Task TakeScreenshotFor(string? targetDevice)
        {
            if (shotBusy || recordClosed) return;
            shotBusy = true;
            UpdateRecStatus();
            try
            {
                var current = ReadMonitors();
                var available = current?.Where(m => targetDevice == null || m.Device == targetDevice).ToList();
                if (available == null || available.Count == 0)
                {
                    NotifyChange("Монитор недоступен", "", "Дождитесь появления изображения и повторите снимок");
                    return;
                }
                // Ask before starting any capture. Cancel creates no files and starts no FFmpeg.
                var games = available.ToDictionary(m => m.Device, GameOn);
                var selected = available.Count > 1
                    ? await ChooseMonitors("Какой монитор снять?", "Выберите экраны. Снимок будет сделан после закрытия этого окна.", available)
                    : available;
                if (selected == null || selected.Count == 0 || recordClosed || shutdownStarted) return;
                await Task.Delay(80); // Let the compositor remove the picker and monitor badges.
                if (recordClosed || shutdownStarted) return;
                var saved = new List<string>();
                foreach (var m in selected)
                {
                    if (recordClosed || shutdownStarted) break;
                    saved.Add(await TrackRecordingSave(Svc(m).ScreenshotAsync(BuildOptions(m), games[m.Device], m.Bounds)));
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
            finally { shotBusy = false; UpdateRecStatus(); }
        }

        private void Screenshot_Click(object sender, RoutedEventArgs e) => _ = TakeScreenshot();

        public Task SaveReplay() => SaveReplayFor(null);

        private async Task SaveReplayFor(string? targetDevice)
        {
            if (recBusy || recordClosed) return;
            var buffered = ReplayableMonitors.Where(m => targetDevice == null || m.Device == targetDevice).ToList();
            if (buffered.Count == 0)
            {
                if (settings.ReplayEnabled)
                    NotifyChange("Повтор ещё не готов", "", "Подождите, пока появятся кадры захвата");
                else NotifyChange("Повторы выключены", "", "Включите повторы в карточке монитора");
                return;
            }
            recBusy = true;
            UpdateRecStatus();
            try
            {
                var chosen = buffered.Count > 1
                    ? await ChooseMonitors("Какой повтор сохранить?", $"Последние {DurationText(settings.ReplaySeconds)} с выбранного монитора.", buffered)
                    : buffered;
                if (chosen == null || recordClosed) return;
                UpdateRecStatus();
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

                UpdateRecStatus();
            }
        }

        private void StartManualCapture(MonitorItem monitor, RecordOptions options)
        {
            string device = monitor.Device;
            nextRecordAttempt[device] = Environment.TickCount64 + CaptureRetryMs;
            string game = GameOn(monitor);
            Svc(monitor).StartRecording(options, game);
            if (!manualCaptures.ContainsKey(device)) manualStartTimes[device] = DateTime.Now;
            manualCaptures[device] = options;
            startGame[device] = game;
            recSeconds.Remove(device);
        }

        public Task ToggleRecording() => ToggleRecordingFor(null);

        private async Task ToggleRecordingFor(string? targetDevice)
        {
            if (recBusy || recordClosed) return;
            recBusy = true;
            UpdateRecStatus();
            try
            {
                if (targetDevice == null ? AnyRecording : manualCaptures.ContainsKey(targetDevice))
                {
                    var stopping = manualCaptures.Keys.Where(d => targetDevice == null || d == targetDevice).ToList();
                    foreach (string device in stopping) { manualCaptures.Remove(device); manualStartTimes.Remove(device); nextRecordAttempt.Remove(device); }
                    UpdateRecStatus();
                    var jobs = svcs.Where(kv => kv.Value.HasRecordingSession && (targetDevice == null || kv.Key == targetDevice))
                                   .Select(kv => kv.Value.StopRecordingAsync(GameForRecording(kv.Key))).ToList();
                    var files = (await TrackRecordingSave(Task.WhenAll(jobs))).Where(f => f != null).ToList();
                    NotifyChange(files.Count == 0 ? "Запись остановлена" : files.Count > 1 ? $"Записи сохранены ({files.Count})" : "Запись сохранена",
                                 "", files.Count > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : null);
                }
                else
                {
                    if (!await EnsureRecorderReady() || recordClosed) return;
                    var chosen = targetDevice == null
                        ? await ChooseMonitors("Какой монитор записывать?", "Запись идёт, пока не нажмёте клавишу ещё раз. Alt-Tab её не прерывает.")
                        : monitors.Where(m => m.Device == targetDevice).ToList();
                    if (chosen == null || recordClosed) return;
                    var latest = ReadMonitors();
                    chosen = latest?.Where(m => chosen.Any(previous => previous.Device == m.Device)).ToList();
                    if (chosen == null || chosen.Count == 0)
                    {
                        NotifyChange("Монитор недоступен", "", "Дождитесь появления изображения и начните запись снова");
                        return;
                    }
                    if (!AnyRecording) { startGame.Clear(); recSeconds.Clear(); }
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
                        foreach (var m in started)
                        {
                            manualCaptures.Remove(m.Device); manualStartTimes.Remove(m.Device); nextRecordAttempt.Remove(m.Device);
                            _ = SaveInterruptedRecording(svcs[m.Device], startGame[m.Device]);
                        }
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
            _ = OrganizeRecordingFolderAsync(RecFolder);
            RecSettingsChanged();
        }

        public bool RecordingActive => AnyRecording;
        public bool ReplayActive => ReplayableMonitors.Any();

        private void UpdateRecStatus()
        {
            if (RecStatus == null) return;
            foreach (var (device, card) in recordingCards)
            {
                svcs.TryGetValue(device, out var service);
                bool recording = manualCaptures.ContainsKey(device);
                card.Update(settings.ReplayEnabled && settings.RecordMonitors?.Contains(device) == true,
                    service?.BufferReceivingFrames == true, service?.BufferedSeconds ?? 0, service?.CanSaveReplay == true,
                    recording, recording && service?.Recording != true, recBusy || startingReplay, shotBusy,
                    DateTime.Now - manualStartTimes.GetValueOrDefault(device, DateTime.Now));
            }
            RecInfo.Text = "При первом запуске записи скачается FFmpeg (~110 МБ).";
            RecInfo.Visibility = FfmpegInstaller.Installed ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetRecorderMessage(string text)
        {
            RecStatus.Text = text;
            RecStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string DurationText(int s) => DurationLabel(s, accusative: true);
    }
}
