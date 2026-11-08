using System;
using System.Windows;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // Equalizer APO: equalizer for headphones without the C-Media driver.
    public partial class MainWindow
    {
        private enum ApoStep { Install, Attach, Reboot }
        private ApoStep apoStep;
        private bool apoBusy;

        /// <summary>Shows the right Equalizer APO step for a device that has no equalizer yet.</summary>
        private void UpdateApoOffer()
        {
            if (ApoOffer == null || apoBusy) return;
            bool show = session != null && session.Eq == null;
            ApoOffer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            // with an offer on screen keep the overlay compact
            bool compact = show || DriverOffer.Visibility == Visibility.Visible;
            EqUnavailableIcon.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            EqUnavailableText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            if (!show) return;

            if (apoStep == ApoStep.Reboot) return; // keep "restart" until the user does it
            if (!EqApoController.Installed)
            {
                apoStep = ApoStep.Install;
                ApoTitle.Text = "Эквалайзер для любых наушников";
                ApoText.Text = "IO Companion поставит бесплатный Equalizer APO и будет управлять им сам. " +
                               "В его окне один раз отметьте свои наушники, потом перезагрузите компьютер.";
                ApoButtonText.Text = "Включить эквалайзер";
            }
            else
            {
                apoStep = ApoStep.Attach;
                ApoTitle.Text = "Equalizer APO установлен, но не подключён к этим наушникам";
                ApoText.Text = "Откроется окно Equalizer APO — отметьте в нём эти наушники и нажмите OK, затем перезагрузите компьютер. " +
                               "Если только что уже отметили — просто перезагрузите компьютер.";
                ApoButtonText.Text = "Подключить к этим наушникам";
            }
        }

        private async void ApoButton_Click(object sender, RoutedEventArgs e)
        {
            if (apoBusy) return;
            if (apoStep == ApoStep.Reboot)
            {
                if (MessageBox.Show(this, "Перезагрузить компьютер сейчас? Сохраните открытые документы.", "IO Companion",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    EqApoInstaller.Reboot();
                return;
            }

            apoBusy = true;
            ApoButton.IsEnabled = false;
            try
            {
                bool ok;
                if (apoStep == ApoStep.Install)
                {
                    ApoText.Text = "Скачиваю Equalizer APO с официальной страницы и проверяю файл…";
                    var progress = new Progress<double>(p => ApoButtonText.Text = $"Загрузка {p:P0}");
                    string setup = await EqApoInstaller.DownloadAsync(progress);
                    ApoButtonText.Text = "Установка…";
                    ApoText.Text = "Windows спросит разрешение — нажмите «Да». Затем в окне Equalizer APO " +
                                   "поставьте галочку у своих наушников и нажмите OK.";
                    ok = await EqApoInstaller.InstallAsync(setup);
                }
                else
                {
                    ApoButtonText.Text = "Жду выбор устройства…";
                    ApoText.Text = "Windows спросит разрешение — нажмите «Да», затем отметьте наушники в окне Equalizer APO.";
                    ok = await EqApoInstaller.SelectDevicesAsync();
                }

                if (ok && EqApoController.Installed)
                {
                    apoStep = ApoStep.Reboot;
                    ApoTitle.Text = "Почти готово — осталось перезагрузить компьютер";
                    ApoText.Text = "Windows подключит эквалайзер к наушникам после перезагрузки. После неё IO Companion сразу начнёт им управлять.";
                    ApoButtonText.Text = "Перезагрузить";
                }
                else
                {
                    ApoText.Text = "Не получилось (возможно, вы нажали «Нет» в запросе Windows). Можно попробовать ещё раз.";
                    ApoButtonText.Text = apoStep == ApoStep.Install ? "Включить эквалайзер" : "Подключить к этим наушникам";
                }
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                ApoText.Text = "Ошибка: " + ex.Message;
                ApoButtonText.Text = "Попробовать ещё раз";
            }
            finally
            {
                apoBusy = false;
                ApoButton.IsEnabled = true;
            }
        }
    }
}
