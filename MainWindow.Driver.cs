using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // Official driver install for io Graphite headsets + removing Audio Center from autostart.
    public partial class MainWindow
    {
        private DispatcherTimer? downloadWatcher;
        private DateTime downloadWatchStart;
        private bool driverBusy;

        private void InitDriver()
        {
            DriverModelCombo.ItemsSource = DriverInstaller.Models;
            DriverModelCombo.SelectedIndex = 0;
            AudioCenterAutostartButton.Visibility = DriverInstaller.AudioCenterAutostarts ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Offer the driver only for io Graphite headsets that run without the C-Media driver.</summary>
        private void UpdateDriverOffer()
        {
            if (DriverOffer == null) return;
            bool offer = session != null && session.Eq == null &&
                         session.Info.Name.Contains("Graphite", StringComparison.OrdinalIgnoreCase);
            DriverOffer.Visibility = offer ? Visibility.Visible : Visibility.Collapsed;
            if (offer && !driverBusy) DriverModelCombo.SelectedItem = DriverInstaller.Guess(session!.Info.Name);
        }

        private async void DriverInstall_Click(object sender, RoutedEventArgs e)
        {
            if (driverBusy || DriverModelCombo.SelectedItem is not DriverModel model) return;
            SetDriverBusy(true, "Скачиваю…");
            DriverStatus.Text = "Скачиваю официальный установщик с Яндекс Диска IO…";

            var progress = new Progress<double>(p => DriverInstallText.Text = $"{p:P0}");
            string? setup = await DriverInstaller.TryDownloadAsync(model, progress)
                            ?? DriverInstaller.FindDownloaded(model);
            if (setup != null)
            {
                await InstallDriver(setup);
                return;
            }

            // Yandex refused the automatic download - let the user click "Download", then pick the file up.
            DriverInstaller.OpenOfficialFolder();
            DriverStatus.Text = $"Яндекс Диск не дал скачать автоматически. В открывшейся папке откройте «{model.FolderPrefix}…» " +
                                "и нажмите «Скачать» у файла .exe — программа сама найдёт его в «Загрузках» и продолжит установку.";
            DriverInstallText.Text = "Жду файл…";
            downloadWatchStart = DateTime.Now.AddMinutes(-1);
            downloadWatcher?.Stop();
            downloadWatcher = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            long lastSize = -1;
            downloadWatcher.Tick += async (_, _) =>
            {
                if ((DateTime.Now - downloadWatchStart).TotalMinutes > 15)
                {
                    downloadWatcher.Stop();
                    SetDriverBusy(false, "Установить драйвер");
                    DriverStatus.Text = "Файл так и не появился в «Загрузках». Нажмите «Установить драйвер», чтобы попробовать ещё раз.";
                    return;
                }
                string? file = DriverInstaller.FindDownloaded(model, downloadWatchStart);
                if (file == null) return;
                long size = new System.IO.FileInfo(file).Length;
                if (size != lastSize) { lastSize = size; return; } // still being written
                downloadWatcher.Stop();
                await InstallDriver(file);
            };
            downloadWatcher.Start();
        }

        private async Task InstallDriver(string setup)
        {
            SetDriverBusy(true, "Установка…");
            DriverStatus.Text = "Устанавливаю драйвер. Windows спросит разрешение — нажмите «Да».";
            bool ok = await DriverInstaller.InstallAsync(setup);
            SetDriverBusy(false, "Установить драйвер");
            AudioCenterAutostartButton.Visibility = DriverInstaller.AudioCenterAutostarts ? Visibility.Visible : Visibility.Collapsed;
            if (!ok)
            {
                DriverStatus.Text = "Установка не завершилась (возможно, отказано в правах администратора). Можно попробовать ещё раз.";
                return;
            }
            DriverStatus.Text = "Готово! Выключите и включите наушники (или переподключите донгл) — эквалайзер появится сам.";
            NotifyChange("Драйвер установлен", "", "Переподключите наушники");
            deviceSignature = "";
            for (int i = 0; i < 10 && session?.Eq == null; i++)
            {
                await Task.Delay(3000);
                RefreshDevices(force: true);
            }
        }

        private void SetDriverBusy(bool busy, string buttonText)
        {
            driverBusy = busy;
            DriverInstallButton.IsEnabled = !busy;
            DriverModelCombo.IsEnabled = !busy;
            DriverInstallText.Text = buttonText;
        }

        private async void AudioCenterAutostart_Click(object sender, RoutedEventArgs e)
        {
            AudioCenterAutostartButton.IsEnabled = false;
            bool ok = await DriverInstaller.RemoveAudioCenterAutostartAsync();
            AudioCenterAutostartButton.IsEnabled = true;
            if (ok && !DriverInstaller.AudioCenterAutostarts)
            {
                AudioCenterAutostartButton.Visibility = Visibility.Collapsed;
                NotifyChange("Audio Center убран из автозагрузки", "");
            }
        }
    }
}
