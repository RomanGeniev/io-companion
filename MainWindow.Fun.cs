using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // AutoEq presets, per-program volume and overlay.
    public partial class MainWindow
    {
        private int mixerTick;

        /// <summary>Called from the 1-second poll timer.</summary>
        private void FunTick()
        {
            // re-apply remembered program volumes every 3 s (only if the user set any)
            if (++mixerTick % 3 == 0 && session != null && settings.AppVolumes.Count > 0)
            {
                try { AppVolumeMixer.ApplyRemembered(session.Info.Id, settings.AppVolumes); }
                catch (Exception ex) { SettingsStore.LogError(ex); }
            }
        }

        // ===================== AutoEq =====================

        private void AutoEq_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AutoEqWindow(this);
            if (dlg.ShowDialog() != true || dlg.ResultBands == null) return;
            string name = dlg.ResultName.Length > 40 ? dlg.ResultName[..40] : dlg.ResultName;
            settings.Custom.RemoveAll(c => c.Name == name);
            settings.Custom.Add(new CustomPreset { Name = name, Bands = dlg.ResultBands });
            settings.ActiveCustom = name;
            RefreshCustomList();
            LoadPresetToUi();
            ScheduleSave();
            NotifyChange("Пресет добавлен", "", name);
        }

        // ===================== Per-program volume =====================

        private void RefreshMixer()
        {
            MixerList.Children.Clear();
            if (session == null) { MixerEmpty.Visibility = Visibility.Visible; return; }
            List<AppVolumeInfo> apps;
            try { apps = AppVolumeMixer.List(session.Info.Id); }
            catch (Exception ex) { SettingsStore.LogError(ex); apps = new(); }
            MixerEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            string deviceId = session.Info.Id;

            foreach (var app in apps)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

                row.Children.Add(AppIcons.Tile(app.IconPath, app.IsSystemSounds));
                var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                title.Children.Add(new TextBlock { Text = app.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var sub = new TextBlock
                {
                    Text = "громкость запомнена",
                    Visibility = !app.IsSystemSounds && settings.AppVolumes.ContainsKey(app.Process) ? Visibility.Visible : Visibility.Collapsed,
                    FontSize = 11,
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                title.Children.Add(sub);
                Grid.SetColumn(title, 1);
                row.Children.Add(title);

                var mute = new System.Windows.Controls.Primitives.ToggleButton
                {
                    IsChecked = app.Muted, Margin = new Thickness(8, 0, 10, 0),
                    Content = new TextBlock { Text = app.Muted ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 },
                    ToolTip = "Выключить звук программы",
                };
                mute.SetResourceReference(StyleProperty, "IconToggle");
                Grid.SetColumn(mute, 2);
                row.Children.Add(mute);

                var slider = new Slider { Value = app.Volume, VerticalAlignment = VerticalAlignment.Center };
                HelpTips.Set(slider, "Громкость только этой программы на выбранном устройстве. Остальные программы не меняются. Для обычных приложений уровень запоминается; 100% убирает дополнительное уменьшение громкости.");
                slider.SetResourceReference(StyleProperty, "VolumeSlider");
                Grid.SetColumn(slider, 3);
                row.Children.Add(slider);

                var value = new TextBlock { Text = $"{app.Volume}%", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(value, 4);
                row.Children.Add(value);

                string process = app.Process;
                slider.ValueChanged += (_, ev) =>
                {
                    int v = (int)Math.Round(ev.NewValue);
                    value.Text = $"{v}%";
                    try { AppVolumeMixer.Set(deviceId, process, v, null); } catch { }
                    if (!app.IsSystemSounds)
                    {
                        if (v == 100) settings.AppVolumes.Remove(process); else settings.AppVolumes[process] = v;
                        sub.Visibility = settings.AppVolumes.ContainsKey(process) ? Visibility.Visible : Visibility.Collapsed;
                        ScheduleSave();
                    }
                };
                mute.Click += (_, _) =>
                {
                    bool m = mute.IsChecked == true;
                    ((TextBlock)mute.Content).Text = m ? "" : "";
                    HelpTips.Set(mute, m ? "Включить звук этой программы. Остальные приложения не изменятся." : "Выключить звук только этой программы. Уровень её громкости сохраняется.");
                    try { AppVolumeMixer.Set(deviceId, process, null, m); } catch { }
                };

                HelpTips.Set(mute, app.Muted ? "Включить звук этой программы. Остальные приложения не изменятся." : "Выключить звук только этой программы. Уровень её громкости сохраняется.");

                var card = new Border { Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12), Child = row };
                card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
                MixerList.Children.Add(card);
            }
        }

        private void MixerOpen_Click(object sender, RoutedEventArgs e)
        {
            MixerPopup.IsOpen = !MixerPopup.IsOpen;
        }

        private void MixerPopup_Opened(object sender, EventArgs e)
        {
            RefreshMixer();
            MixerRefreshButton.Focus();
        }

        private void MixerClose_Click(object sender, RoutedEventArgs e) => MixerPopup.IsOpen = false;

        private void MixerPopup_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            MixerPopup.IsOpen = false;
            MixerOpenButton.Focus();
            e.Handled = true;
        }

        private void MixerRefresh_Click(object sender, RoutedEventArgs e) => RefreshMixer();

        public string? CurrentDeviceId => session?.Info.Id;

        /// <summary>The overlay switched the Windows output: follow it with the equalizer / presets too.</summary>
        public void SelectDeviceFromOutside(string deviceId)
        {
            settings.DeviceId = deviceId;
            ScheduleSave();
            RefreshDevices(force: true);
            NotifyChange("Звук: " + (session?.Info.Name ?? ""), "");
        }

        /// <summary>Sets a program's volume on the selected device and (optionally) remembers it.</summary>
        public void SetAppVolume(string process, int volume, bool remember)
        {
            if (session == null) return;
            try { AppVolumeMixer.Set(session.Info.Id, process, volume, null); } catch { }
            if (!remember) return;
            if (volume == 100) settings.AppVolumes.Remove(process); else settings.AppVolumes[process] = volume;
            ScheduleSave();
        }

        // ===================== Overlay (Win+G style) =====================

        private OverlayWindow? overlay;
        private OverlayTimer? countdownTimer;
        public OverlayTimer CountdownTimer
        {
            get
            {
                if (countdownTimer == null)
                {
                    countdownTimer = new OverlayTimer();
                    countdownTimer.Completed += () =>
                    {
                        if (shutdownStarted) return;
                        Beeper.TimerFinished();
                        NotifyChange("Время вышло", "\uE916", "Таймер завершён");
                    };
                }
                return countdownTimer;
            }
        }

        public void ToggleOverlay()
        {
            if (regionSelection != null) return;
            if (shutdownStarted) return;
            if (overlay?.ActivateOwnedDialog() == true) return;
            if (overlay is { IsVisible: true }) { overlay.Close(); return; }
            overlay = new OverlayWindow(this);
            overlay.Closed += (_, _) => { overlay = null; ScheduleTrim(); if (MixerPopup.IsOpen) RefreshMixer(); };
            overlay.Open();
        }

    }
}
