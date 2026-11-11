using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    public partial class MainWindow : Window
    {
        private const double ThumbHeight = 14;
        private const string ImportedPresetName = "Из Audio Center";

        private readonly AppSettings settings;
        private readonly Slider[] bandSliders = new Slider[EqBands.Count];
        private readonly TextBlock[] bandValues = new TextBlock[EqBands.Count];
        private readonly int[] bands = new int[EqBands.Count];
        private readonly Canvas curveCanvas = new() { IsHitTestVisible = false, ClipToBounds = false };
        private readonly Path curveStroke = new();
        private readonly Path curveFill = new();
        private readonly Line zeroLine = new();
        private readonly ObservableCollection<string> customNames = new();
        private readonly DispatcherTimer applyTimer;
        private readonly DispatcherTimer saveTimer;
        private readonly DispatcherTimer pollTimer;
        private readonly TrayService tray;
        private readonly HotkeyService hotkeys;

        private readonly List<EffectCard> outputCards = new();
        private readonly List<EffectCard> micCards = new();
        private readonly Dictionary<(bool mic, Guid format, uint pid), int> pendingEffects = new();
        private readonly DispatcherTimer effectTimer;
        private AudioDeviceSession? micSession;
        private bool suppressMicVolume;
        private bool updatingMicChoices;
        private List<AudioDeviceInfo> activeInputs = new();
        private sealed record MicrophoneChoice(string? Id, string? Name, string Label);

        private AudioDeviceSession? session;
        private int[]? lastWrittenBands;
        private bool? lastWrittenEnabled;
        private string deviceSignature = "";
        private int pollTick;
        private bool initialized;
        private bool suppress;
        private bool suppressVolume;
        private bool suppressCombo;
        private bool reallyExit;
        private bool shutdownStarted, shutdownComplete;
        private bool trayHintShown;
        private readonly Services.BatteryService battery = new();
        private Services.BatteryService.Level? batteryLevel;

        public MainWindow()
        {
            InitializeComponent();

            bool firstRun = !SettingsStore.Exists;
            settings = SettingsStore.Load();
            ThemeManager.Apply(settings.Theme, settings.Accent);

            applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
            applyTimer.Tick += (_, _) => { applyTimer.Stop(); ApplyToDevice(); };
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); SettingsStore.Save(settings); };
            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            pollTimer.Tick += (_, _) => Poll();
            effectTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            effectTimer.Tick += (_, _) => { effectTimer.Stop(); FlushEffects(); };

            BuildBands();
            BuildEffectCards();
            TabEq.IsChecked = true;
            CustomList.ItemsSource = customNames;
            RefreshCustomList();

            tray = new TrayService();
            Icon = tray.CreateWindowIcon();
            tray.OpenRequested += ShowFromTray;
            tray.ExitRequested += ExitApp;
            tray.ToggleEqRequested += () => ToggleEq(notify: false);
            tray.ModeRequested += m => SelectMode(m, notify: false);
            tray.EarpadRequested += p => SelectEarpad(p, notify: false);

            hotkeys = new HotkeyService(this);
            RegisterHotkeys();
            InitExtras();

            AutostartSwitch.IsChecked = Autostart.IsEnabled;
            MinimizeSwitch.IsChecked = settings.MinimizeToTray;
            NotifySwitch.IsChecked = settings.Notifications;
            EqSwitch.IsChecked = settings.EqEnabled;

            RefreshDevices(force: true);
            if (firstRun) ImportCurrentDeviceEq();

            LoadPresetToUi();
            InitFeatures();
            InitAutoClicker();
            RefreshPushToTalk();
            HelpTips.Prepare(this);
            initialized = true;

            Closing += OnClosing;
            Activated += (_, _) => ReloadEffects();
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(DrawCurve), DispatcherPriority.Loaded);
            pollTimer.Start();
        }

        // ===================== Equalizer UI =====================

        private void BuildBands()
        {
            for (int i = 0; i < EqBands.Count; i++)
                BandsGrid.ColumnDefinitions.Add(new ColumnDefinition());

            Grid.SetRow(curveCanvas, 1);
            Grid.SetColumnSpan(curveCanvas, EqBands.Count);
            BandsGrid.Children.Add(curveCanvas);

            zeroLine.SetResourceReference(Shape.StrokeProperty, "ZeroLineBrush");
            zeroLine.StrokeThickness = 1;
            zeroLine.StrokeDashArray = new DoubleCollection { 4, 4 };
            curveFill.SetResourceReference(Shape.FillProperty, "CurveFillBrush");
            curveStroke.SetResourceReference(Shape.StrokeProperty, "AccentGradient");
            curveStroke.StrokeThickness = 2.5;
            curveStroke.StrokeLineJoin = PenLineJoin.Round;
            curveCanvas.Children.Add(zeroLine);
            curveCanvas.Children.Add(curveFill);
            curveCanvas.Children.Add(curveStroke);

            var sliderStyle = (Style)FindResource("EqSlider");

            for (int i = 0; i < EqBands.Count; i++)
            {
                var value = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Text = "0",
                };
                Grid.SetColumn(value, i);
                Grid.SetRow(value, 0);
                BandsGrid.Children.Add(value);
                bandValues[i] = value;

                var slider = new Slider { Style = sliderStyle, Tag = i, HorizontalAlignment = HorizontalAlignment.Center };
                string region = i < 3 ? "Низкие частоты: бас и гул." : i < 6 ? "Средние частоты: тело инструментов и голоса." : "Высокие частоты: яркость, согласные и шипящие.";
                HelpTips.Set(slider, region + " Вверх — усилить эту полосу, вниз — ослабить; 0 дБ — без изменения. Колесо мыши меняет значение на 1 дБ, двойной щелчок возвращает 0. Изменения слышны при включённом эквалайзере.");
                slider.ValueChanged += Band_ValueChanged;
                slider.PreviewMouseWheel += (s, e) =>
                {
                    var sl = (Slider)s;
                    sl.Value = Math.Clamp(sl.Value + (e.Delta > 0 ? 1 : -1), sl.Minimum, sl.Maximum);
                    e.Handled = true;
                };
                slider.MouseDoubleClick += (s, _) => ((Slider)s).Value = 0;
                Grid.SetColumn(slider, i);
                Grid.SetRow(slider, 1);
                BandsGrid.Children.Add(slider);
                bandSliders[i] = slider;

                var freq = new TextBlock
                {
                    Text = EqBands.Labels[i] + (i < 5 ? " Гц" : ""),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    FontSize = 12,

                };
                Grid.SetColumn(freq, i);
                Grid.SetRow(freq, 2);
                freq.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                BandsGrid.Children.Add(freq);
            }

            curveCanvas.SizeChanged += (_, _) => DrawCurve();
        }

        private void Band_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            var slider = (Slider)sender;
            int i = (int)slider.Tag;
            bands[i] = (int)Math.Round(e.NewValue);
            UpdateBandLabel(i);
            DrawCurve();
            if (suppress || !initialized) return;
            UpdateClipInfo();

            StoreCurrentBands();
            UpdatePresetTitle();
            ScheduleApply();
            ScheduleSave();
        }

        private void UpdateBandLabel(int i)
        {
            int v = bands[i];
            bandValues[i].Text = v > 0 ? "+" + v : v < 0 ? "−" + (-v) : "0";
            bandValues[i].SetResourceReference(TextBlock.ForegroundProperty, v == 0 ? "SubTextBrush" : "TextBrush");
        }

        private void DrawCurve()
        {
            if (curveCanvas.ActualWidth <= 0 || !curveCanvas.IsVisible) return;
            var pts = new Point[EqBands.Count];
            double zeroY = 0;
            for (int i = 0; i < EqBands.Count; i++)
            {
                var s = bandSliders[i];
                if (s == null || s.ActualHeight <= 0) return;
                double travel = s.ActualHeight - ThumbHeight;
                double y = ThumbHeight / 2 + (s.Maximum - bands[i]) / (s.Maximum - s.Minimum) * travel;
                pts[i] = s.TranslatePoint(new Point(s.ActualWidth / 2, y), curveCanvas);
                if (i == 0)
                    zeroY = s.TranslatePoint(new Point(0, ThumbHeight / 2 + s.Maximum / (s.Maximum - s.Minimum) * travel), curveCanvas).Y;
            }

            zeroLine.X1 = 0;
            zeroLine.X2 = curveCanvas.ActualWidth;
            zeroLine.Y1 = zeroLine.Y2 = zeroY;

            var figure = new PathFigure { StartPoint = pts[0] };
            for (int i = 0; i < pts.Length - 1; i++)
            {
                // Catmull-Rom -> cubic Bezier for a smooth response curve.
                Point p0 = pts[Math.Max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.Min(i + 2, pts.Length - 1)];
                figure.Segments.Add(new BezierSegment(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2, true));
            }
            curveStroke.Data = new PathGeometry(new[] { figure });

            var fill = figure.Clone();
            fill.Segments.Add(new LineSegment(new Point(pts[^1].X, zeroY), false));
            fill.Segments.Add(new LineSegment(new Point(pts[0].X, zeroY), false));
            fill.IsClosed = true;
            curveFill.Data = new PathGeometry(new[] { fill });
        }

        // ===================== Effects & microphone =====================

        private void BuildEffectCards()
        {
            foreach (var def in Effects.Output)
            {
                var card = new EffectCard(def);
                card.Changed += OnOutputEffectChanged;
                outputCards.Add(card);
                OutputEffectsGrid.Children.Add(card);
            }
            foreach (var def in Effects.Microphone)
            {
                var card = new EffectCard(def);
                card.Changed += (f, p, v) => QueueEffect(true, f, p, v);
                micCards.Add(card);
                MicEffectsGrid.Children.Add(card);
            }
        }

        private void QueueEffect(bool mic, Guid format, uint pid, int value)
        {
            pendingEffects[(mic, format, pid)] = value;
            effectTimer.Stop();
            effectTimer.Start();
        }

        private void FlushEffects()
        {
            foreach (var ((mic, format, pid), value) in pendingEffects.ToList())
            {
                var store = (mic ? micSession : session)?.Effects;
                if (store == null) continue;
                try { store.Write(format, pid, value); }
                catch (Exception ex)
                {
                    SettingsStore.LogError(ex);
                    BannerText.Text = "Не удалось применить эффект: " + ex.Message;
                    Banner.Visibility = Visibility.Visible;
                }
            }
            pendingEffects.Clear();
            ReloadEffects();
        }

        /// <summary>Re-reads effect settings from the driver (they can also be changed in Audio Center).</summary>
        private void ReloadEffects()
        {
            if (pendingEffects.Count > 0) return;
            var outStore = session?.Effects;
            foreach (var c in outputCards)
            {
                c.SetAvailable(outStore != null);
                }
            if (outStore != null)
            {
                CaptureEffectsFromDevice(outStore);
                foreach (var c in outputCards) c.Load(CurrentEffect);
            }
            var micStore = micSession?.Effects;
            foreach (var c in micCards)
            {
                bool supported = micStore != null && IsSupported(micStore, c.Definition);
                c.SetAvailable(supported);
                c.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
                if (supported) c.Load(micStore!.Read);
            }
            // other headsets may support only some of the C-Media effects - hide the rest
            foreach (var c in outputCards) c.Visibility = IsSupported(outStore, c.Definition) ? Visibility.Visible : Visibility.Collapsed;
            MicEffectsGrid.Visibility = micCards.Any(c => c.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            UpdateCapabilityUi();
        }

        private static bool IsSupported(ApoPropertyStore? store, EffectDef def) =>
            store == null || store.Read(def.Format, def.EnablePid).HasValue;

        private void RefreshMicrophones()
        {
            try { activeInputs = AudioDevices.ListActiveInputs(); }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                if (micSession == null) MicName.Text = "Не удалось получить список входов Windows.";
                return;
            }
            ApplyMicrophoneSelection();
        }

        private void ApplyMicrophoneSelection()
        {
            string? selectedId = settings.MicrophoneId;
            var paired = AudioDevices.FindMatchingInput(session?.Info, activeInputs);
            string? windowsDefaultId = selectedId == null && paired == null && activeInputs.Count > 0
                ? DefaultDevice.Get(Native.DataFlowCapture) : null;
            var target = AudioDevices.SelectInput(activeInputs, selectedId, paired, windowsDefaultId);
            if (selectedId != null && target != null && settings.MicrophoneName != target.Name)
            {
                settings.MicrophoneName = target.Name;
                if (initialized) ScheduleSave();
            }
            UpdateMicrophoneChoices(activeInputs, paired);

            bool changed = !AudioDevices.SameId(micSession?.Info.Id, target?.Id);
            if (changed)
            {
                CloseMicrophone();
                if (target != null)
                {
                    try { micSession = AudioDevices.Open(target); }
                    catch (Exception ex) { SettingsStore.LogError(ex); }
                }
            }

            RefreshPushToTalk();
            UpdateMicrophoneControls(target);
            if (!changed) return;
            ReloadEffects();
            PollMic();
            UpdateMeters();
            if (PageMic.Visibility == Visibility.Visible) UpdateNrCard();
            flyout?.Refresh();
        }

        private void UpdateMicrophoneChoices(List<AudioDeviceInfo> inputs, AudioDeviceInfo? paired)
        {
            var choices = new List<MicrophoneChoice> { new(null, null, "Авто · гарнитура или вход Windows") };
            foreach (var input in inputs)
            {
                string label = AudioDevices.SameId(input.Id, paired?.Id) ? input.Name + " · гарнитура" : input.Name;
                choices.Add(new MicrophoneChoice(input.Id, input.Name, label));
            }
            string? selectedId = settings.MicrophoneId;
            if (selectedId != null && !choices.Any(choice => AudioDevices.SameId(choice.Id, selectedId)))
            {
                string savedName = settings.MicrophoneName ?? "Выбранный микрофон";
                choices.Add(new MicrophoneChoice(selectedId, savedName, savedName + " · отключён"));
            }

            updatingMicChoices = true;
            try
            {
                if (MicDeviceCombo.ItemsSource is List<MicrophoneChoice> current && current.SequenceEqual(choices))
                    choices = current;
                else
                    MicDeviceCombo.ItemsSource = choices;
                MicDeviceCombo.SelectedItem = choices.First(choice => AudioDevices.SameId(choice.Id, selectedId));
            }
            finally { updatingMicChoices = false; }
        }

        private void UpdateMicrophoneControls(AudioDeviceInfo? target)
        {
            MicName.Visibility = micSession != null && settings.MicrophoneId != null ? Visibility.Collapsed : Visibility.Visible;
            if (micSession != null)
                MicName.Text = $"Сейчас: {target?.Name ?? micSession.Info.Name}";
            else if (target != null)
                MicName.Text = "Не удалось открыть этот микрофон.";
            else
                MicName.Text = settings.MicrophoneId == null ? "Подключите микрофон к компьютеру." : "Ожидаю подключения выбранного микрофона.";

            bool hasVolume = micSession?.HasVolume == true;
            MicVolumeSlider.IsEnabled = MicMuteButton.IsEnabled = hasVolume;
            MonitorSwitch.IsEnabled = micSession != null && MonitorOutputId() != null;
            if (hasVolume) return;
            MicVolumeText.Text = "—";
            UpdateMicMuteUi(false);
            SyncTrayMic(false);
        }

        private void CloseMicrophone()
        {
            DetachPushToTalk();
            // Finish any queued driver writes while they still point to the old endpoint.
            FlushMicVoiceSettings();
            FlushEffects();
            if (MicVolumeSlider.IsMouseCaptureWithin) Mouse.Capture(null);
            tests.StopMonitor();
            MonitorSwitch.IsChecked = false;
            micMeter.Stop();
            micSession?.Dispose();
            micSession = null;
        }

        private void PollMic()
        {
            if (micSession == null || !micSession.HasVolume) return;
            try
            {
                float vol = micSession.GetVolume();
                bool mute = micSession.GetMute();
                if (!MicVolumeSlider.IsMouseCaptureWithin)
                {
                    suppressMicVolume = true;
                    try { MicVolumeSlider.Value = Math.Round(vol * 100); }
                    finally { suppressMicVolume = false; }
                    MicVolumeText.Text = $"{(int)Math.Round(vol * 100)}%";
                }
                UpdateMicMuteUi(mute);
                SyncTrayMic(mute);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                CloseMicrophone();
                UpdateMicrophoneControls(null);
                ReloadEffects();
                UpdateMeters();
                if (PageMic.Visibility == Visibility.Visible) UpdateNrCard();
                flyout?.Refresh();
            }
        }

        private void UpdateMicMuteUi(bool muted)
        {
            MicMuteButton.IsChecked = muted;
            MicMuteIcon.Text = muted ? "" : "";
            HelpTips.Set(MicMuteButton, (muted ? "Включить микрофон. " : "Выключить микрофон. ") + "Переключает вход выбранного микрофона в Windows, а не только его прослушивание здесь.");
        }

        private void MicVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MicVolumeText == null) return;
            MicVolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (suppressMicVolume || micSession == null) return;
            try { micSession.SetVolume((float)(e.NewValue / 100.0)); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        private void MicMute_Click(object sender, RoutedEventArgs e)
        {
            if (micSession == null) { MicMuteButton.IsChecked = false; return; }
            ToggleMicMute(fromHotkey: false);
        }

        private void ResetEffects_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in outputCards) c.ResetToDefaults();
        }

        private GalleryPanel? galleryPanel;

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            string tab = (string)((FrameworkElement)sender).Tag;
            PageEq.Visibility = tab == "Eq" ? Visibility.Visible : Visibility.Collapsed;
            PageEffects.Visibility = tab == "Effects" ? Visibility.Visible : Visibility.Collapsed;
            PageMic.Visibility = tab == "Mic" ? Visibility.Visible : Visibility.Collapsed;
            if (MixerPopup != null) MixerPopup.IsOpen = false;
            PageRecord.Visibility = tab == "Record" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = tab == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            if (tab == "Gallery" && galleryPanel == null) PageGallery.Content = galleryPanel = new GalleryPanel(() => RecFolder);
            PageGallery.Visibility = tab == "Gallery" ? Visibility.Visible : Visibility.Collapsed;
            PageAutoClicker.Visibility = tab == "AutoClicker" ? Visibility.Visible : Visibility.Collapsed;
            OutputStrip.Visibility = tab is "Eq" or "Effects" ? Visibility.Visible : Visibility.Collapsed;
            (PageTitle.Text, PageDescription.Text) = tab switch
            {
                "Gallery" => ("Галерея", "Последние записи, повторы и скриншоты."),
                "AutoClicker" => ("Автокликер", "Настройте клики. Запускайте и останавливайте одной клавишей."),
                "Mic" => ("Микрофон", "Выберите микрофон, проверьте уровень и послушайте результат."),
                "Record" => ("Запись экрана", "Видео, мгновенные повторы и скриншоты."),
                "Effects" => ("Эффекты", "Включайте нужные эффекты и сравнивайте результат на слух."),
                "Settings" => ("Настройки", "Оформление, запуск приложения и уведомления."),
                _ => ("Звук", "Выберите настроение. Подстройте детали под себя."),
            };
            if (tab == "Mic")
            {
                if (initialized) RefreshMicrophones();
                UpdateNrCard();
            }
            if (tab == "Record") UpdateRecStatus();
            if (initialized)
                AnimatePage(tab switch { "Eq" => PageEq, "Effects" => PageEffects, "Mic" => PageMic, "Record" => PageRecord, "Settings" => PageSettings, "AutoClicker" => PageAutoClicker, "Gallery" => PageGallery, _ => PageEq });
            if (tab == "Eq") Dispatcher.BeginInvoke(new Action(DrawCurve), DispatcherPriority.Loaded);
            UpdateMeters();
        }

        // ===================== Presets =====================

        /// <summary>Headphones or speakers for the selected device (remembered per device).</summary>
        private DeviceKind CurrentKind
        {
            get
            {
                if (session == null) return DeviceKind.Headphones;
                return settings.DeviceKinds.TryGetValue(session.Info.Id, out var k)
                    ? k
                    : SpeakerPresets.Guess(session.Info.Name, session.Eq is CmEqController);
            }
        }

        private bool IsSpeakers => CurrentKind == DeviceKind.Speakers;

        private string CurrentKey => IsSpeakers ? SpeakerPresets.Key(settings.Mode) : FactoryPresets.Key(settings.Earpad, settings.Mode);

        private int[] FactoryBands() => IsSpeakers ? SpeakerPresets.Get(settings.Mode) : FactoryPresets.Get(settings.Earpad, settings.Mode);

        private CustomPreset? ActiveCustom =>
            settings.ActiveCustom == null ? null : settings.Custom.FirstOrDefault(c => c.Name == settings.ActiveCustom);

        private int[] CurrentPresetBands()
        {
            if (settings.ActiveCustom != null)
            {
                var custom = ActiveCustom;
                if (custom != null && custom.Bands.Length == EqBands.Count) return (int[])custom.Bands.Clone();
                settings.ActiveCustom = null;
            }
            if (settings.Edited.TryGetValue(CurrentKey, out var edited) && edited.Length == EqBands.Count)
                return (int[])edited.Clone();
            return FactoryBands();
        }

        private string? draftCopy; // auto-created copy that keeps following the edit in progress

        private void StoreCurrentBands()
        {
            var custom = ActiveCustom;
            if (custom != null)
            {
                // a program's own sound ("Игра: …") is meant to be tuned in place
                if (custom.Name == draftCopy || custom.Name.StartsWith("Игра: ")) { custom.Bands = (int[])bands.Clone(); return; }
                if (bands.SequenceEqual(custom.Bands)) return;
                // saved presets are never overwritten: any change goes into a new copy
                string baseName = custom.Name, name = baseName + " (копия)";
                for (int n = 2; settings.Custom.Any(c => c.Name == name); n++) name = $"{baseName} (копия {n})";
                settings.Custom.Add(new CustomPreset
                {
                    Name = name, Bands = (int[])bands.Clone(),
                });
                settings.ActiveCustom = name;
                draftCopy = name;
                bool was = suppress;
                suppress = true;
                RefreshCustomList();
                CustomList.SelectedItem = name;
                DeleteCustomButton.IsEnabled = true;
                suppress = was;
                NotifyChange("Новый пресет «" + name + "»", "", $"«{baseName}» не изменён");
                return;
            }
            if (bands.SequenceEqual(FactoryBands()))
                settings.Edited.Remove(CurrentKey);
            else
                settings.Edited[CurrentKey] = (int[])bands.Clone();
        }

        private void LoadPresetToUi()
        {
            draftCopy = null;
            var src = CurrentPresetBands();
            suppress = true;
            for (int i = 0; i < EqBands.Count; i++)
            {
                bands[i] = src[i];
                bandSliders[i].Value = src[i];
                UpdateBandLabel(i);
            }

            PadFabric.IsChecked = settings.Earpad == Earpad.Fabric;
            PadLeatherette.IsChecked = settings.Earpad == Earpad.Leatherette;
            PadVelour.IsChecked = settings.Earpad == Earpad.Velour;
            bool builtIn = settings.ActiveCustom == null;
            ModeGames.IsChecked = builtIn && settings.Mode == ListeningMode.Games;
            ModeMusic.IsChecked = builtIn && settings.Mode == ListeningMode.Music;
            ModeMovies.IsChecked = builtIn && settings.Mode == ListeningMode.Movies;
            CustomList.SelectedItem = settings.ActiveCustom;
            DeleteCustomButton.IsEnabled = CustomList.SelectedItem != null;
            HelpTips.Set(PadCardTitle, IsSpeakers ? SpeakerPresets.Hint : FactoryPresets.Hint(settings.Earpad));
            KindHeadphones.IsChecked = !IsSpeakers;
            KindSpeakers.IsChecked = IsSpeakers;
            PadSelector.Visibility = IsSpeakers ? Visibility.Collapsed : Visibility.Visible;
            PadCardTitle.Text = IsSpeakers ? "УСТРОЙСТВО ЗВУКА" : "АМБУШЮРЫ";
            suppress = false;

            UpdatePresetTitle();
            DrawCurve();
            UpdateClipInfo();
            ScheduleApply();
            if (initialized) StoreDeviceProfile();
            flyout?.Refresh();
        }

        private string PresetTitleText() =>
            settings.ActiveCustom != null
                ? "Мой пресет · " + settings.ActiveCustom
                : $"{(IsSpeakers ? "Колонки" : FactoryPresets.Name(settings.Earpad))} · {FactoryPresets.Name(settings.Mode)}";

        private void UpdatePresetTitle()
        {
            bool edited = settings.ActiveCustom == null && settings.Edited.ContainsKey(CurrentKey);
            PresetTitle.Text = PresetTitleText() + (edited ? " · изменён" : "");
            ResetButton.IsEnabled = edited;
            tray.UpdateState(settings.Earpad, settings.ActiveCustom == null ? settings.Mode : null,
                             settings.EqEnabled, PresetTitleText());
        }

        private void SelectMode(ListeningMode mode, bool notify)
        {
            settings.Mode = mode;
            settings.ActiveCustom = null;
            LoadPresetToUi();
            ScheduleSave();
            if (notify) NotifyChange(PresetTitleText());
        }

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            if (suppress || !initialized || session == null) return;
            var kind = Enum.Parse<DeviceKind>((string)((FrameworkElement)sender).Tag);
            settings.DeviceKinds[session.Info.Id] = kind;
            settings.ActiveCustom = null;
            LoadPresetToUi();
            ScheduleSave();
        }

        private void SelectEarpad(Earpad pad, bool notify)
        {
            if (IsSpeakers) return; // earpads only make sense for headphones
            settings.Earpad = pad;
            settings.ActiveCustom = null;
            LoadPresetToUi();
            ScheduleSave();
            if (notify) NotifyChange(PresetTitleText());
        }

        private void ToggleEq(bool notify)
        {
            settings.EqEnabled = !settings.EqEnabled;
            EqSwitch.IsChecked = settings.EqEnabled;
            UpdatePresetTitle();
            ScheduleApply();
            ScheduleSave();
            if (notify) NotifyChange(settings.EqEnabled ? "Эквалайзер включён" : "Эквалайзер выключен", "");
            flyout?.Refresh();
        }

        private void RefreshCustomList()
        {
            customNames.Clear();
            foreach (var c in settings.Custom) customNames.Add(c.Name);
            CustomEmpty.Visibility = customNames.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CustomList.Visibility = customNames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ImportCurrentDeviceEq()
        {
            // First launch: keep the sound exactly as it was configured in the official app.
            if (session?.Eq == null) return;
            try
            {
                var current = session.Eq.ReadBands();
                if (current.All(v => v == 0)) return;
                settings.Custom.RemoveAll(c => c.Name == ImportedPresetName);
                settings.Custom.Insert(0, new CustomPreset { Name = ImportedPresetName, Bands = current });
                settings.ActiveCustom = ImportedPresetName;
                settings.EqEnabled = session.Eq.ReadEnabled();
                EqSwitch.IsChecked = settings.EqEnabled;
                RefreshCustomList();
                SettingsStore.Save(settings);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
            }
        }

        // ===================== Device =====================

        private void ScheduleApply()
        {
            applyTimer.Stop();
            applyTimer.Start();
        }

        private void ScheduleSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        private void ApplyToDevice()
        {
            var eq = session?.Eq;
            if (eq == null) return;
            try
            {
                bool wantEnabled = settings.EqEnabled && !Comparing; // "Сравнить" keeps the EQ off
                if (lastWrittenEnabled != wantEnabled)
                {
                    eq.SetEnabled(wantEnabled);
                    lastWrittenEnabled = wantEnabled;
                }
                var applied = AppliedBands();
                eq.SetBands(applied, lastWrittenBands);
                lastWrittenBands = applied;
            }
            catch (Exception ex) when (eq is EqApoController)
            {
                // a busy config file is temporary: keep the connection and try again shortly
                SettingsStore.LogError(ex);
                lastWrittenBands = null;
                lastWrittenEnabled = null;
                ScheduleApply();
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                CloseSession();
                SetStatus(false, "Не удалось передать настройки драйверу. Пробую переподключиться…");
                deviceSignature = "";
            }
        }

        private void Poll()
        {
            if (overlay is { IsDragging: true }) return;
            if (++pollTick % 3 == 0 || session == null) RefreshDevices(force: false);
            if (pollTick % 3 == 0) RefreshMicrophones();
            if (pollTick % 60 == 0 || batteryLevel == null && pollTick % 15 == 0) UpdateBattery();
            PollMic();
            if (initialized) FunTick();
            if (session == null || !session.HasVolume) return;
            try
            {
                float vol = session.GetVolume();
                bool mute = session.GetMute();
                if (!VolumeSlider.IsMouseCaptureWithin)
                {
                    suppressVolume = true;
                    VolumeSlider.Value = Math.Round(vol * 100);
                    suppressVolume = false;
                }
                UpdateMuteUi(mute);
            }
            catch
            {
                CloseSession();
                deviceSignature = "";
            }
        }

        /// <summary>Refreshes the battery hint of the current device; hidden when it does not report one.
        /// HID/GATT reads can take hundreds of milliseconds, so they run on a worker thread
        /// and only the UI update happens here.</summary>
        private volatile bool batteryReading;

        private void UpdateBattery()
        {
            if (session == null || batteryReading) return;
            var info = session.Info;
            batteryReading = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                Services.BatteryService.Level? level = null;
                try { level = battery.Read(info.Id, info.Name); }
                catch (Exception ex) { SettingsStore.LogError(ex); }
                Dispatcher.BeginInvoke(() =>
                {
                    batteryReading = false;
                    bool changed = !Equals(level, batteryLevel);
                    batteryLevel = level;
                    if (!changed) return;
                    UpdateBatteryUi();
                    if (flyoutIsVisible()) flyout?.Refresh();
                    if (level != null && battery.ShouldNotifyLow(level, out string msg))
                        NotifyChange(msg);
                });
            });
        }

        private bool flyoutIsVisible() => flyout != null && flyout.IsVisible;

        private void UpdateBatteryUi()
        {
            if (BatteryText == null) return;
            if (batteryLevel == null) { BatteryText.Text = ""; BatteryText.Visibility = Visibility.Collapsed; return; }
            BatteryText.Text = $"🔋 {batteryLevel.Percent}%";
            BatteryText.Visibility = Visibility.Visible;
            BatteryText.SetResourceReference(TextBlock.ForegroundProperty, batteryLevel.Percent <= 20 ? "WarnBrush" : "SubTextBrush");
        }

        private void RefreshDevices(bool force)
        {
            List<AudioDeviceInfo> list;
            try { list = AudioDevices.ListActiveOutputs(); }
            catch (Exception ex) { SettingsStore.LogError(ex); list = new List<AudioDeviceInfo>(); }

            string signature = string.Join("|", list.Select(d => d.Id + ":" + d.HasCmEq));
            if (!force && signature == deviceSignature) return;
            deviceSignature = signature;

            var target = list.FirstOrDefault(d => d.Id == settings.DeviceId)
                         ?? list.FirstOrDefault(d => d.HasCmEq);

            suppressCombo = true;
            DeviceCombo.ItemsSource = list;
            DeviceCombo.SelectedItem = target;
            suppressCombo = false;

            OpenSession(target);
            RefreshMicrophones();
        }

        private void OpenSession(AudioDeviceInfo? info)
        {
            if (info != null && session != null && session.Info.Id == info.Id && session.Eq != null == info.HasCmEq)
                return;

            CloseSession();
            if (info == null)
            {
                SetStatus(false, "Наушники не найдены. Включите их и проверьте, что радиомодуль подключён.");
                ShowBanner("Наушники не подключены — настройки применятся автоматически, как только они появятся.");
                return;
            }

            try
            {
                session = AudioDevices.Open(info);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                SetStatus(false, "Не удалось открыть устройство.");
                return;
            }

            lastWrittenBands = null;
            lastWrittenEnabled = null;

            if (session.Eq != null)
            {
                SetStatus(true, session.Eq is CmEqController ? "Подключено · встроенный эквалайзер C-Media" : "Подключено · эквалайзер Equalizer APO");
                ShowBanner(null);
                ScheduleApply();
            }
            else
            {
                SetStatus(true, "Подключено · эквалайзер недоступен");
                ShowBanner("Нет встроенного эквалайзера C-Media — остальные функции работают.");
            }
            EqSwitch.IsEnabled = session.Eq != null;
            BandsGrid.Opacity = session.Eq != null ? 1 : 0.45;
            ReloadEffects();
            if (initialized) { CompareOff(); RestoreDeviceProfile(); UpdateBrand(); UpdateMeters(); LoadPresetToUi(); }
            Dispatcher.BeginInvoke(new Action(UpdateBattery), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CloseSession()
        {
            FlushEffects();
            tests.StopMonitor();
            tests.StopChannelTest();
            analyzer.Stop();
            if (MonitorSwitch != null) MonitorSwitch.IsChecked = false;
            session?.Dispose();
            session = null;
            VolumeText.Text = "—";
            batteryLevel = null;
            if (BatteryText != null) BatteryText.Visibility = Visibility.Collapsed;
            ReloadEffects();
            if (initialized) { UpdateBrand(); UpdateMeters(); }
        }

        private void SetStatus(bool ok, string text)
        {
            StatusDot.SetResourceReference(Shape.FillProperty, ok ? "GoodBrush" : "WarnBrush");
            StatusText.Text = text;
        }

        private void ShowBanner(string? text)
        {
            Banner.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            BannerText.Text = text ?? "";
        }

        private void UpdateMuteUi(bool muted)
        {
            MuteButton.IsChecked = muted;
            MuteIcon.Text = muted ? "" : "";
            HelpTips.Set(MuteButton, (muted ? "Включить звук. " : "Выключить звук. ") + "Временно отключает звук выбранного устройства. Положение регулятора громкости сохраняется.");
        }

        // ===================== Hotkeys / tray / window =====================

        private void RegisterHotkeys()
        {
            // Win+G: taken from the Xbox Game Bar with a keyboard hook; the configurable combination always works as well
            winG = new WinGHook();
            winG.Pressed += () => Dispatcher.BeginInvoke(new Action(ToggleOverlay));
            winG.EscapePressed = () =>
            {
                if (regionSelection != null) { Dispatcher.BeginInvoke(new Action(() => regionSelection?.Cancel())); return true; }
                if (IsAutoClickerKeyRecording) return false;
                if (AutoClickerState.Running) { StopAutoClicker(); return true; }
                var current = overlay;
                if (current is not { IsVisible: true }) return false;
                if (current.HasOpenDialog) return false;
                Dispatcher.BeginInvoke(new Action(() => { if (current.IsVisible) current.DismissFromEscape(); }));
                return true;
            };
            ApplyHotkeys();
        }

        private string hotkeyLine = "";
        private WinGHook? winG;

        private void SetOverlayHotkeyLabel(string over)
        {
            HotkeysText.Text = $"Оверлей: {over}";
            Controls.HelpTips.Set(HotkeysSettingsButton, $"Горячие клавиши работают из любой программы. {hotkeyLine}");
        }

        public void ShowFromTray()
        {
            if (shutdownStarted) return;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void ExitApp()
        {
            reallyExit = true;
            Close();
        }

        private async void OnClosing(object? sender, CancelEventArgs e)
        {
            if (shutdownComplete) return;
            if (!reallyExit && settings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    tray.Notify("SoundShot работает в трее",
                                "Пресеты переключаются горячими клавишами. Выход — правый клик по значку.");
                    trayHintShown = true;
                }
                return;
            }

            e.Cancel = true;
            if (shutdownStarted) return;
            shutdownStarted = true;
            galleryPanel?.Dispose();
            countdownTimer?.Dispose();
            autoClicker?.Dispose();
            autoClickerUiTimer?.Stop();
            regionSelection?.Cancel();
            clickerIndicator?.Close();
            DetachPushToTalk();
            reallyExit = true;
            Hide();
            tray.Dispose();
            hotkeys.Dispose();
            winG?.Dispose();
            pollTimer.Stop();
            applyTimer.Stop();
            saveTimer.Stop();
            effectTimer.Stop();
            updateTimer?.Stop();
            trimTimer?.Stop();
            await Task.Yield();
            foreach (var child in Application.Current.Windows.Cast<Window>().Where(window => window != this).ToArray())
            {
                try { child.Close(); }
                catch (Exception ex) { SettingsStore.LogError(ex); }
            }
            var recorderShutdown = DisposeRecordAsync();
            try
            {
                ApplyToDevice();
                CloseMicrophone();
                DisposeExtras();
                DisposeFeatures();
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            try { await recorderShutdown; }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            SettingsStore.Save(settings);
            try { CloseSession(); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            shutdownComplete = true;
            Application.Current.Shutdown();
        }

        // ===================== Event handlers =====================

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (suppress || !initialized) return;
            SelectMode(Enum.Parse<ListeningMode>((string)((FrameworkElement)sender).Tag), notify: false);
        }

        private void Pad_Checked(object sender, RoutedEventArgs e)
        {
            if (suppress || !initialized) return;
            SelectEarpad(Enum.Parse<Earpad>((string)((FrameworkElement)sender).Tag), notify: false);
        }

        private void CustomList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DeleteCustomButton.IsEnabled = CustomList.SelectedItem != null;
            if (suppress || !initialized) return;
            if (CustomList.SelectedItem is string name)
            {
                settings.ActiveCustom = name;
                LoadPresetToUi();
                ScheduleSave();
            }
        }

        private void SaveCustom_Click(object sender, RoutedEventArgs e) => SaveCustom();

        private void CustomName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) SaveCustom();
        }

        private void SaveCustom()
        {
            string name = CustomName.Text.Trim();
            if (name.Length == 0)
            {
                int n = 1;
                while (settings.Custom.Any(c => c.Name == $"Мой пресет {n}")) n++;
                name = $"Мой пресет {n}";
            }

            var existing = settings.Custom.FirstOrDefault(c => c.Name == name);
            if (existing != null) existing.Bands = (int[])bands.Clone();
            else settings.Custom.Add(new CustomPreset { Name = name, Bands = (int[])bands.Clone() });

            settings.ActiveCustom = name;
            CustomName.Text = "";
            RefreshCustomList();
            LoadPresetToUi();
            ScheduleSave();
        }

        private void DeleteCustom_Click(object sender, RoutedEventArgs e)
        {
            if (CustomList.SelectedItem is not string name) return;
            if (new Controls.ConfirmPresetWindow(this, name).ShowDialog() != true) return;
            settings.Custom.RemoveAll(c => c.Name == name);
            if (settings.ActiveCustom == name) settings.ActiveCustom = null;
            RefreshCustomList();
            LoadPresetToUi();
            ScheduleSave();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (settings.ActiveCustom != null) return;
            settings.Edited.Remove(CurrentKey);
            LoadPresetToUi();
            ScheduleSave();
        }

        private void Flat_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in bandSliders) s.Value = 0;
        }

        private void EqSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (!initialized) return;
            settings.EqEnabled = EqSwitch.IsChecked == true;
            UpdatePresetTitle();
            ScheduleApply();
            ScheduleSave();
        }

        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressCombo || !initialized) return;
            if (DeviceCombo.SelectedItem is AudioDeviceInfo info)
            {
                settings.DeviceId = info.Id;
                OpenSession(info);
                RefreshMicrophones();
                ScheduleSave();
            }
        }

        private void MicDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingMicChoices || !initialized || MicDeviceCombo.SelectedItem is not MicrophoneChoice choice) return;
            settings.MicrophoneId = choice.Id;
            settings.MicrophoneName = choice.Name;
            ApplyMicrophoneSelection();
            ScheduleSave();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText == null) return;
            VolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (suppressVolume || session == null) return;
            try { session.SetVolume((float)(e.NewValue / 100.0)); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (session == null) { MuteButton.IsChecked = false; return; }
            try
            {
                bool mute = !session.GetMute();
                session.SetMute(mute);
                UpdateMuteUi(mute);
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        private void AutostartSwitch_Click(object sender, RoutedEventArgs e)
        {
            try { Autostart.Set(AutostartSwitch.IsChecked == true); }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                AutostartSwitch.IsChecked = Autostart.IsEnabled;
            }
        }

        private void MinimizeSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.MinimizeToTray = MinimizeSwitch.IsChecked == true;
            ScheduleSave();
        }

        private void NotifySwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.Notifications = NotifySwitch.IsChecked == true;
            ScheduleSave();
        }
    }
}
