using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // Update check (GitHub releases) and one-click install.
    public partial class MainWindow
    {
        private UpdateInfo? availableUpdate;
        private DispatcherTimer? updateTimer;
        private bool installing;

        private void InitUpdates()
        {
            UpdatesSwitch.IsChecked = settings.CheckUpdates;
            VersionText.Text = UpdateService.Configured
                ? $"Версия {UpdateService.CurrentText}"
                : $"Версия {UpdateService.CurrentText} · обновления не настроены";
            CheckUpdateButton.IsEnabled = UpdateService.Configured;

            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) }; // first check soon after start
            updateTimer.Tick += async (_, _) =>
            {
                updateTimer.Interval = TimeSpan.FromHours(6);
                if (settings.CheckUpdates) await CheckForUpdate(manual: false);
            };
            updateTimer.Start();
        }

        private async Task CheckForUpdate(bool manual)
        {
            if (!UpdateService.Configured || installing) return;
            try
            {
                if (manual) { CheckUpdateButton.IsEnabled = false; VersionText.Text = "Проверяю…"; }
                var info = await UpdateService.CheckAsync();
                availableUpdate = info;
                if (info != null)
                {
                    UpdateTitle.Text = $"Доступна версия {info.Version.ToString(3)}";
                    UpdateNotes.Text = info.Notes.Replace("\r", " ").Replace("\n", " ").Trim();
                    UpdateNotes.Visibility = UpdateNotes.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                    UpdateCard.Visibility = Visibility.Visible;
                    if (!manual && settings.SkippedVersion != info.Version.ToString(3))
                        NotifyChange("Доступно обновление", "", $"Версия {info.Version.ToString(3)} — откройте приложение");
                }
                VersionText.Text = info == null
                    ? $"Версия {UpdateService.CurrentText} · последняя"
                    : $"Версия {UpdateService.CurrentText} → {info.Version.ToString(3)}";
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                if (manual) VersionText.Text = $"Версия {UpdateService.CurrentText} · нет связи с сервером";
            }
            finally
            {
                CheckUpdateButton.IsEnabled = UpdateService.Configured;
            }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            if (availableUpdate == null || installing) return;
            installing = true;
            UpdateButton.IsEnabled = false;
            try
            {
                var progress = new Progress<double>(p => UpdateButtonText.Text = $"{p:P0}");
                UpdateButtonText.Text = "Загрузка…";
                await UpdateService.InstallAsync(availableUpdate, progress);
                UpdateButtonText.Text = "Перезапуск…";
                await Task.Delay(400);
                ExitApp(); // the update script waits for us to exit, swaps the exe and starts it again
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                installing = false;
                UpdateButton.IsEnabled = true;
                UpdateButtonText.Text = "Обновить";
                MessageBox.Show(this, "Не удалось обновиться: " + ex.Message, Title);
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdate(manual: true);

        private void UpdatesSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.CheckUpdates = UpdatesSwitch.IsChecked == true;
            ScheduleSave();
        }
    }
}
