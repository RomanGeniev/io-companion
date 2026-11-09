using System;
using System.Windows;
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

        /// <summary>The microphone to clean up: the headset's own mic, otherwise the Windows default one.</summary>
        private (string id, string name, int channels)? NrMic()
        {
            try
            {
                var en = new MMDeviceEnumerator();
                MMDevice dev = micSession != null
                    ? en.GetDevice(micSession.Info.Id)
                    : en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                return (dev.ID, dev.FriendlyName, dev.AudioClient.MixFormat.Channels);
            }
            catch
            {
                return null;
            }
        }

        private void UpdateNrCard()
        {
            if (NrCard == null || nrBusy) return;
            var mic = NrMic();
            if (mic == null)
            {
                NrText.Text = "Микрофон не найден.";
                NrSwitch.Visibility = NrButton.Visibility = Visibility.Collapsed;
                return;
            }
            string who = $"Микрофон: {mic.Value.name}.";
            bool hasOwnAi = micSession?.Effects != null && micCards.Exists(c => c.Visibility == Visibility.Visible);

            if (!EqApoController.Installed) nrStep = NrStep.InstallApo;
            else if (!EqApoController.IsAttached(mic.Value.id)) nrStep = nrStep == NrStep.Reboot ? NrStep.Reboot : NrStep.AttachMic;
            else nrStep = NrStep.Ready;

            NrSwitch.Visibility = nrStep == NrStep.Ready ? Visibility.Visible : Visibility.Collapsed;
            NrButton.Visibility = nrStep == NrStep.Ready ? Visibility.Collapsed : Visibility.Visible;
            string intro = "Нейросеть RNNoise убирает клавиатуру, вентиляторы и фоновый шум. " +
                           (hasOwnAi ? "У этой гарнитуры есть и своё AI-шумоподавление выше — включайте что-то одно. " : "");
            switch (nrStep)
            {
                case NrStep.InstallApo:
                    NrText.Text = intro + who + " Нужен бесплатный Equalizer APO: в его окне отметьте этот микрофон на вкладке «Устройства записи» (Capture devices), затем перезагрузите компьютер.";
                    NrButtonText.Text = "Установить";
                    break;
                case NrStep.AttachMic:
                    NrText.Text = intro + who + " Откроется окно Equalizer APO — на вкладке «Устройства записи» (Capture devices) отметьте этот микрофон и нажмите OK, затем перезагрузите компьютер.";
                    NrButtonText.Text = "Подключить микрофон";
                    break;
                case NrStep.Reboot:
                    NrText.Text = intro + who + " Осталось перезагрузить компьютер.";
                    NrButtonText.Text = "Перезагрузить";
                    break;
                case NrStep.Ready:
                    NrSwitch.IsChecked = EqApoController.MicNoiseSuppressionOn(mic.Value.id);
                    NrText.Text = intro + who + " Проверьте с «Слышать себя» ниже.";
                    break;
            }
        }

        private async void NrButton_Click(object sender, RoutedEventArgs e)
        {
            if (nrBusy) return;
            if (nrStep == NrStep.Reboot)
            {
                if (MessageBox.Show(this, "Перезагрузить компьютер сейчас? Сохраните открытые документы.", "SoundShot",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    EqApoInstaller.Reboot();
                return;
            }
            nrBusy = true;
            NrButton.IsEnabled = false;
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
                if (ok) nrStep = NrStep.Reboot;
                else NrText.Text = "Не получилось (возможно, вы нажали «Нет» в запросе Windows). Попробуйте ещё раз.";
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NrText.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                nrBusy = false;
                NrButton.IsEnabled = true;
                UpdateNrCard();
            }
        }

        private async void NrSwitch_Click(object sender, RoutedEventArgs e)
        {
            var mic = NrMic();
            if (mic == null || nrBusy) return;
            bool on = NrSwitch.IsChecked == true;
            nrBusy = true;
            NrSwitch.IsEnabled = false;
            try
            {
                if (on && !EqApoInstaller.RnnoiseInstalled)
                {
                    NrText.Text = "Скачиваю модуль шумоподавления RNNoise (один раз, ~36 МБ)… Windows спросит разрешение — «Да».";
                    if (!await EqApoInstaller.InstallRnnoiseAsync())
                    {
                        NrSwitch.IsChecked = false;
                        NrText.Text = "Модуль не установлен (возможно, вы нажали «Нет»). Попробуйте ещё раз.";
                        return;
                    }
                }
                string? plugin = on ? EqApoInstaller.RnnoisePath(mic.Value.channels >= 2) : null;
                if (!EqApoController.SetMicNoiseSuppression(mic.Value.id, plugin))
                {
                    NrSwitch.IsChecked = !on;
                    NrText.Text = "Equalizer APO не подключён к этому микрофону.";
                    return;
                }
                NotifyChange(on ? "Шумоподавление включено" : "Шумоподавление выключено", "");
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                NrSwitch.IsChecked = !on;
                NrText.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                nrBusy = false;
                NrSwitch.IsEnabled = true;
                if (NrText.Text.StartsWith("Скачиваю")) UpdateNrCard();
            }
        }
    }
}
