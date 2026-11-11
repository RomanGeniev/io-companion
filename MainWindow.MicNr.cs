using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Services;
using NAudio.CoreAudioApi;

namespace GraphiteCompanion
{
    // RNNoise noise suppression for any microphone, through Equalizer APO.
    public partial class MainWindow
    {
        private enum NrStep { InstallApo, AttachMic, Reboot, Ready }
        private NrStep nrStep;
        private bool nrBusy;
        private string? nrRebootMicId;
        private string? voiceDeviceId;
        private bool loadingVoice;
        private DispatcherTimer? voiceSaveTimer;
        private (string Id, MicVoiceSettings Voice)? pendingVoice;
        private const int DefaultMicSampleRate = 48000;
        private const double VoicePlotMinFrequency = 30;
        private const double VoicePlotMinDb = -48;
        private const double VoicePlotMaxDb = 24;
        private static readonly int[] VoicePlotFrequencies = [50, 100, 300, 1000, 3000, 10000, 20000];
        private static readonly int[] VoicePlotLevels = [24, 0, -24, -48];
        private int voiceSampleRate = DefaultMicSampleRate;
        private MicVoiceSettings plottedVoice = new();

        private (string id, string name, int channels, int sampleRate)? NrMic()
        {
            var info = micSession?.Info;
            if (info == null) return null;
            try
            {
                using var en = new MMDeviceEnumerator();
                using var dev = en.GetDevice(info.Id);
                using var client = dev.AudioClient;
                var format = client.MixFormat;
                return (info.Id, info.Name, format.Channels, format.SampleRate);
            }
            catch
            {
                return null;
            }
        }

        private void UpdateNrCard()
        {
            if (NrCard == null) return;
            var mic = micSession?.Info;
            if (nrBusy)
            {
                if (!AudioDevices.SameId(voiceDeviceId, mic?.Id)) UpdateMicVoiceCard(ready: false);
                return;
            }
            if (mic == null)
            {
                NrText.Text = "Микрофон не найден.";
                NrSwitch.Visibility = NrButton.Visibility = Visibility.Collapsed;
                UpdateMicVoiceCard(ready: false);
                return;
            }
            string who = $"Выбранный микрофон: {mic.Name}.";
            bool hasOwnAi = micSession?.Effects != null && micCards.Exists(c => c.Visibility == Visibility.Visible);

            if (!EqApoController.Installed) nrStep = NrStep.InstallApo;
            else if (!EqApoController.IsAttached(mic.Id)) nrStep = AudioDevices.SameId(nrRebootMicId, mic.Id) ? NrStep.Reboot : NrStep.AttachMic;
            else nrStep = NrStep.Ready;
            UpdateMicVoiceCard(nrStep == NrStep.Ready);

            NrSwitch.Visibility = nrStep == NrStep.Ready ? Visibility.Visible : Visibility.Collapsed;
            NrButton.Visibility = nrStep == NrStep.Ready ? Visibility.Collapsed : Visibility.Visible;
            string intro = "Нейросеть RNNoise убирает клавиатуру, вентиляторы и фоновый шум. " +
                           (hasOwnAi ? "У этой гарнитуры есть и своё AI-шумоподавление — включайте что-то одно. " : "");
            GraphiteCompanion.Controls.HelpTips.Set(NrCard, intro + who + " Для сравнения включите «Слышать себя» в наушниках.");
            switch (nrStep)
            {
                case NrStep.InstallApo:
                    NrText.Text = "Установите Equalizer APO, отметьте микрофон в «Устройствах записи» и перезагрузите компьютер.";
                    NrButtonText.Text = "Установить";
                    break;
                case NrStep.AttachMic:
                    NrText.Text = "В Equalizer APO отметьте микрофон в «Устройствах записи», нажмите OK и перезагрузите компьютер.";
                    NrButtonText.Text = "Подключить микрофон";
                    break;
                case NrStep.Reboot:
                    NrText.Text = "Для завершения подключения перезагрузите компьютер.";
                    NrButtonText.Text = "Перезагрузить";
                    break;
                case NrStep.Ready:
                    NrSwitch.IsChecked = EqApoController.MicNoiseSuppressionOn(mic.Id);
                    NrText.Text = hasOwnAi ? "Используйте один шумодав: RNNoise или AI гарнитуры." : "Уменьшает клавиатуру и фоновый шум.";
                    break;
            }
        }

        private void UpdateMicVoiceCard(bool ready)
        {
            var mic = micSession?.Info;
            voiceDeviceId = ready ? mic?.Id : null;
            voiceSampleRate = voiceDeviceId != null ? NrMic()?.sampleRate ?? DefaultMicSampleRate : DefaultMicSampleRate;
            MicVoiceSwitch.IsEnabled = MicVoiceControls.IsEnabled = voiceDeviceId != null;
            if (voiceDeviceId == null)
            {
                LoadMicVoiceValues(new MicVoiceSettings());
                SetMicVoiceStatus(mic == null ? "Выберите подключённый микрофон." : "Подключите микрофон к Equalizer APO слева.");
                return;
            }

            try
            {
                var voice = pendingVoice is { } pending && AudioDevices.SameId(pending.Id, voiceDeviceId)
                    ? pending.Voice : EqApoController.GetMicVoice(voiceDeviceId);
                LoadMicVoiceValues(voice);
                SetMicVoiceStatus(null);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                voiceDeviceId = null;
                MicVoiceSwitch.IsEnabled = MicVoiceControls.IsEnabled = false;
                LoadMicVoiceValues(new MicVoiceSettings());
                SetMicVoiceStatus("Не удалось прочитать настройки Equalizer APO.");
            }
        }

        private void LoadMicVoiceValues(MicVoiceSettings voice)
        {
            loadingVoice = true;
            try
            {
                MicVoiceSwitch.IsChecked = voice.Enabled;
                MicVoiceGain.Value = voice.GainDb;
                MicVoiceClarity.Value = voice.ClarityDb;
                MicVoiceTreble.Value = voice.TrebleDb;
                MicVoiceHighPass.SelectedValue = voice.HighPassHz.ToString(CultureInfo.InvariantCulture);
            }
            finally { loadingVoice = false; }
            plottedVoice = voice;
            DrawMicVoicePlot();
        }

        private void SetMicVoiceStatus(string? message)
        {
            MicVoiceStatus.Text = message ?? string.Empty;
            MicVoiceStatus.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void MicVoice_Changed(object sender, RoutedEventArgs e)
        {
            if (!initialized || loadingVoice || voiceDeviceId == null) return;
            int.TryParse(MicVoiceHighPass.SelectedValue as string, out int highPass);
            QueueMicVoice(new MicVoiceSettings(MicVoiceSwitch.IsChecked == true,
                (int)MicVoiceGain.Value, highPass, (int)MicVoiceClarity.Value, (int)MicVoiceTreble.Value));
        }

        private void MicVoiceSwitch_Click(object sender, RoutedEventArgs e)
        {
            MicVoice_Changed(sender, e);
            FlushMicVoiceSettings();
        }

        private void MicVoicePreset_Click(object sender, RoutedEventArgs e)
        {
            if (voiceDeviceId == null) return;
            var voice = ((FrameworkElement)sender).Tag switch
            {
                "Speech" => new MicVoiceSettings(true, -3, 100, 3, 0),
                "Soft" => new MicVoiceSettings(true, 0, 80, 1, -3),
                _ => new MicVoiceSettings(),
            };
            LoadMicVoiceValues(voice);
            QueueMicVoice(voice);
            FlushMicVoiceSettings();
        }

        private void QueueMicVoice(MicVoiceSettings voice)
        {
            if (voiceDeviceId == null || !AudioDevices.SameId(voiceDeviceId, micSession?.Info.Id)) return;
            pendingVoice = (voiceDeviceId, voice);
            if (plottedVoice != voice)
            {
                plottedVoice = voice;
                DrawMicVoicePlot();
            }
            if (voiceSaveTimer == null)
            {
                voiceSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                voiceSaveTimer.Tick += (_, _) => FlushMicVoiceSettings();
            }
            voiceSaveTimer.Stop();
            voiceSaveTimer.Start();
        }

        private void FlushMicVoiceSettings()
        {
            voiceSaveTimer?.Stop();
            if (pendingVoice is not { } pending) return;
            pendingVoice = null;
            bool current = AudioDevices.SameId(pending.Id, voiceDeviceId);
            try
            {
                if (!EqApoController.SetMicVoice(pending.Id, pending.Voice))
                    throw new InvalidOperationException("Equalizer APO не подключён к микрофону.");
                if (current)
                    SetMicVoiceStatus(null);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                if (!current) return;
                UpdateNrCard();
                SetMicVoiceStatus("Не удалось применить настройки Equalizer APO. " + ex.Message);
            }
        }

        private double VoicePlotMaxFrequency => Math.Min(20000, voiceSampleRate * 0.49);

        private Rect MicVoicePlotBounds => new(36, 9,
            Math.Max(0, MicVoicePlot.ActualWidth - 58), Math.Max(0, MicVoicePlot.ActualHeight - 29));

        private static double MicVoicePlotY(Rect bounds, double db) => bounds.Top +
            (VoicePlotMaxDb - Math.Clamp(db, VoicePlotMinDb, VoicePlotMaxDb)) / (VoicePlotMaxDb - VoicePlotMinDb) * bounds.Height;

        private void DrawMicVoicePlot()
        {
            if (MicVoicePlotCursor == null) return;
            Rect bounds = MicVoicePlotBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            double maxFrequency = VoicePlotMaxFrequency;
            double frequencyRatio = maxFrequency / VoicePlotMinFrequency;
            double logRange = Math.Log(frequencyRatio);
            var grid = new StreamGeometry();
            var zero = new StreamGeometry();
            MicVoicePlotLabels.Children.Clear();
            using (var lines = grid.Open())
            using (var baseline = zero.Open())
            {
                foreach (int db in VoicePlotLevels)
                {
                    double y = MicVoicePlotY(bounds, db);
                    var line = db == 0 ? baseline : lines;
                    line.BeginFigure(new Point(bounds.Left, y), false, false);
                    line.LineTo(new Point(bounds.Right, y), true, false);
                    AddMicVoicePlotLabel(db.ToString("+0;-0;0"), bounds.Left - 7, y, TextAlignment.Right);
                }
                foreach (int frequency in VoicePlotFrequencies)
                {
                    if (frequency > maxFrequency) break;
                    double x = bounds.Left + Math.Log(frequency / VoicePlotMinFrequency) / logRange * bounds.Width;
                    lines.BeginFigure(new Point(x, bounds.Top), false, false);
                    lines.LineTo(new Point(x, bounds.Bottom), true, false);
                    string label = frequency < 1000 ? frequency.ToString() : $"{frequency / 1000}k";
                    AddMicVoicePlotLabel(label, x, bounds.Bottom + 12, TextAlignment.Center);
                }
            }

            var curve = new StreamGeometry();
            var fill = new StreamGeometry();
            double zeroY = MicVoicePlotY(bounds, 0);
            using (var stroke = curve.Open())
            using (var area = fill.Open())
            {
                const int steps = 180;
                area.BeginFigure(new Point(bounds.Left, zeroY), true, true);
                for (int i = 0; i <= steps; i++)
                {
                    double fraction = (double)i / steps;
                    double frequency = VoicePlotMinFrequency * Math.Pow(frequencyRatio, fraction);
                    double db = plottedVoice.ResponseDb(frequency, voiceSampleRate);
                    var point = new Point(bounds.Left + bounds.Width * fraction, MicVoicePlotY(bounds, db));
                    if (i == 0) stroke.BeginFigure(point, false, false);
                    else stroke.LineTo(point, true, false);
                    area.LineTo(point, true, false);
                }
                area.LineTo(new Point(bounds.Right, zeroY), true, false);
            }
            grid.Freeze();
            zero.Freeze();
            curve.Freeze();
            fill.Freeze();
            MicVoicePlotGrid.Data = grid;
            MicVoicePlotZero.Data = zero;
            MicVoicePlotCurve.Data = curve;
            MicVoicePlotFill.Data = fill;
            MicVoicePlotCurve.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
                plottedVoice.Enabled ? "AccentBrush" : "SubTextBrush");
            MicVoicePlotFill.Opacity = plottedVoice.Enabled ? 1 : 0;
            UpdateMicVoicePlotReadout(Mouse.GetPosition(MicVoicePlot), MicVoicePlot.IsMouseOver);
        }

        private void AddMicVoicePlotLabel(string text, double x, double y, TextAlignment alignment)
        {
            var label = new TextBlock { Text = text, FontSize = 10 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, x - label.DesiredSize.Width / (alignment == TextAlignment.Center ? 2 : 1));
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
            MicVoicePlotLabels.Children.Add(label);
        }

        private void MicVoicePlot_SizeChanged(object sender, SizeChangedEventArgs e) => DrawMicVoicePlot();

        private void MicVoicePlot_MouseMove(object sender, MouseEventArgs e) =>
            UpdateMicVoicePlotReadout(e.GetPosition(MicVoicePlot), true);

        private void MicVoicePlot_MouseLeave(object sender, MouseEventArgs e) =>
            UpdateMicVoicePlotReadout(default, false);

        private void UpdateMicVoicePlotReadout(Point position, bool hovering)
        {
            string status = plottedVoice.Enabled ? "Включена" : "Предпросмотр · выкл.";
            Rect bounds = MicVoicePlotBounds;
            if (!hovering || bounds.Width <= 0 || !bounds.Contains(position))
            {
                MicVoicePlotReadout.Text = status;
                MicVoicePlotCursor.Visibility = Visibility.Collapsed;
                return;
            }
            double fraction = (position.X - bounds.Left) / bounds.Width;
            double frequency = VoicePlotMinFrequency * Math.Pow(VoicePlotMaxFrequency / VoicePlotMinFrequency, fraction);
            double db = plottedVoice.ResponseDb(frequency, voiceSampleRate);
            MicVoicePlotReadout.Text = $"{frequency:0} Гц  ·  {db:+0.0;-0.0;0.0} дБ";
            MicVoicePlotCursor.X1 = MicVoicePlotCursor.X2 = position.X;
            MicVoicePlotCursor.Y1 = bounds.Top;
            MicVoicePlotCursor.Y2 = bounds.Bottom;
            MicVoicePlotCursor.Visibility = Visibility.Visible;
        }

        private async void NrButton_Click(object sender, RoutedEventArgs e)
        {
            string? micId = micSession?.Info.Id;
            if (nrBusy || micId == null) return;
            if (nrStep == NrStep.Reboot)
            {
                if (MessageBox.Show(this, "Перезагрузить компьютер сейчас? Сохраните открытые документы.", "SoundShot",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    EqApoInstaller.Reboot();
                return;
            }
            nrBusy = true;
            NrButton.IsEnabled = false;
            string? error = null;
            try
            {
                bool ok;
                if (nrStep == NrStep.InstallApo)
                {
                    NrButtonText.Text = "Загрузка…";
                    string setup = await EqApoInstaller.DownloadAsync(new Progress<double>(p => NrButtonText.Text = $"Загрузка {p:P0}"));
                    NrButtonText.Text = "Установка…";
                    NrText.Text = "Windows спросит разрешение — «Да». В окне Equalizer APO откройте вкладку «Устройства записи» (Capture devices), отметьте микрофон и нажмите OK.";
                    ok = await EqApoInstaller.InstallAsync(setup);
                }
                else
                {
                    NrButtonText.Text = "Жду выбор…";
                    NrText.Text = "Windows спросит разрешение — «Да». Вкладка «Устройства записи» (Capture devices) → отметьте микрофон → OK.";
                    ok = await EqApoInstaller.SelectDevicesAsync();
                }
                if (ok) nrRebootMicId = micId;
                else error = "Не получилось (возможно, вы нажали «Нет» в запросе Windows). Попробуйте ещё раз.";
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                error = "Ошибка: " + ex.Message;
            }
            finally
            {
                nrBusy = false;
                NrButton.IsEnabled = true;
                UpdateNrCard();
                if (error != null && AudioDevices.SameId(micId, micSession?.Info.Id)) NrText.Text = error;
            }
        }

        private async void NrSwitch_Click(object sender, RoutedEventArgs e)
        {
            var mic = NrMic();
            if (mic == null || nrBusy) return;
            bool on = NrSwitch.IsChecked == true;
            nrBusy = true;
            NrSwitch.IsEnabled = false;
            string? error = null;
            try
            {
                if (on && !EqApoInstaller.RnnoiseInstalled)
                {
                    NrText.Text = "Скачиваю модуль шумоподавления RNNoise (один раз, ~36 МБ)… Windows спросит разрешение — «Да».";
                    if (!await EqApoInstaller.InstallRnnoiseAsync())
                    {
                        error = "Модуль не установлен (возможно, вы нажали «Нет»). Попробуйте ещё раз.";
                        return;
                    }
                }
                string? plugin = on ? EqApoInstaller.RnnoisePath(mic.Value.channels >= 2) : null;
                if (!EqApoController.SetMicNoiseSuppression(mic.Value.id, plugin))
                {
                    error = "Equalizer APO не подключён к этому микрофону.";
                    return;
                }
                NotifyChange(on ? "Шумоподавление включено" : "Шумоподавление выключено", "", mic.Value.name);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                error = "Ошибка: " + ex.Message;
            }
            finally
            {
                nrBusy = false;
                NrSwitch.IsEnabled = true;
                UpdateNrCard();
                if (error != null && AudioDevices.SameId(mic.Value.id, micSession?.Info.Id)) NrText.Text = error;
            }
        }
    }
}
