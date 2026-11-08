using System;
using System.Windows;
using System.Windows.Media;
using GraphiteCompanion.Models;

namespace GraphiteCompanion
{
    /// <summary>Compact control panel that pops up above the tray icon.</summary>
    public partial class TrayFlyout : Window
    {
        private readonly MainWindow main;
        private bool loading;
        private DateTime hiddenAt = DateTime.MinValue;

        public TrayFlyout(MainWindow main)
        {
            InitializeComponent();
            this.main = main;
            Deactivated += (_, _) => HideFlyout();
        }

        /// <summary>Toggles the panel (a click on the tray icon while open closes it).</summary>
        public void Toggle()
        {
            if (IsVisible) { HideFlyout(); return; }
            if ((DateTime.Now - hiddenAt).TotalMilliseconds < 300) return; // the click that just closed it
            Refresh();
            Opacity = 0;
            Show();
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight;
            Opacity = 1;
            Activate();
        }

        private void HideFlyout()
        {
            if (!IsVisible) return;
            hiddenAt = DateTime.Now;
            Hide();
        }

        public void Refresh()
        {
            loading = true;
            var s = main.GetQuickState();
            PresetText.Text = s.Preset;
            DeviceText.Text = s.Device;
            EqSwitch.IsChecked = s.EqEnabled;
            ModeGames.IsChecked = s.Mode == ListeningMode.Games;
            ModeMusic.IsChecked = s.Mode == ListeningMode.Music;
            ModeMovies.IsChecked = s.Mode == ListeningMode.Movies;
            VolumeSlider.Value = s.Volume;
            VolumeText.Text = $"{s.Volume}%";
            MuteButton.IsChecked = s.Muted;
            MuteIcon.Text = s.Muted ? "" : "";
            MicIcon.Text = s.MicMuted ? "" : "";
            MicText.Text = s.MicMuted ? "Микрофон выкл." : "Микрофон вкл.";
            MicButton.SetResourceReference(ForegroundProperty, s.MicMuted ? "MutedFgBrush" : "TextBrush");
            MicButton.IsEnabled = s.HasMic;
            loading = false;
        }

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            main.SelectModeFromOutside(Enum.Parse<ListeningMode>((string)((FrameworkElement)sender).Tag));
            Refresh();
        }

        private void EqSwitch_Click(object sender, RoutedEventArgs e)
        {
            main.ToggleEqFromOutside();
            Refresh();
        }

        private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolumeText == null) return;
            VolumeText.Text = $"{(int)Math.Round(e.NewValue)}%";
            if (!loading) main.SetVolumeFromOutside((int)Math.Round(e.NewValue));
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            main.ToggleMuteFromOutside();
            Refresh();
        }

        private void Mic_Click(object sender, RoutedEventArgs e)
        {
            main.ToggleMicMute(fromHotkey: false);
            Refresh();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            HideFlyout();
            main.ShowFromTray();
        }
    }
}
