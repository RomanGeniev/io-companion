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
    // AutoEq presets, per-game profiles, per-program volume, Discord status.
    public partial class MainWindow
    {
        private DiscordPresence? discord;
        private int mixerTick;

        private void InitFun()
        {
            DiscordSwitch.IsChecked = settings.DiscordEnabled;
            DiscordIdBox.Text = settings.DiscordAppId ?? "";
            DiscordSetup.Visibility = settings.DiscordEnabled ? Visibility.Visible : Visibility.Collapsed;
            UpdateDiscord();
        }

        private void DisposeFun()
        {
            if (discord != null) { _ = discord.ClearAsync(); discord.Dispose(); }
        }

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
            ForgetAutoState();
            settings.ActiveCustom = name;
            RefreshCustomList();
            LoadPresetToUi();
            ScheduleSave();
            NotifyChange("Пресет добавлен", "", name);
        }

        // ===================== Per-game profiles =====================

        /// <summary>Copies the current sound (EQ + effects) into a preset of its own for this program and links the rule to it.</summary>
        private void MakeGameProfile(AppRule rule)
        {
            string name = "Игра: " + (string.IsNullOrEmpty(rule.Title) ? rule.Process : rule.Title);
            if (name.Length > 40) name = name[..40];
            if (!settings.Custom.Any(c => c.Name == name))
            {
                settings.Custom.Add(new CustomPreset
                {
                    Name = name,
                    Bands = (int[])bands.Clone(),
                    Effects = currentEffects.Count > 0 ? new Dictionary<string, int>(currentEffects) : null,
                });
            }
            rule.Target = "custom:" + name;
            RefreshCustomList();
            ScheduleSave();
            MessageBox.Show(this,
                $"Создан пресет «{name}» из текущих настроек.\n\nТеперь при запуске этой программы включится именно он. " +
                "Чтобы настроить звук под неё: выберите этот пресет в «Моих пресетах» и подвигайте эквалайзер и эффекты — всё сохранится в нём.",
                "Свой профиль", MessageBoxButton.OK, MessageBoxImage.Information);
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
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

                var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                title.Children.Add(new TextBlock { Text = app.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                var sub = new TextBlock
                {
                    Text = app.IsSystemSounds ? "уведомления Windows" : app.Process + (settings.AppVolumes.ContainsKey(app.Process) ? " · запомнено" : ""),
                    FontSize = 11,
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                title.Children.Add(sub);
                row.Children.Add(title);

                var mute = new System.Windows.Controls.Primitives.ToggleButton
                {
                    IsChecked = app.Muted, Margin = new Thickness(8, 0, 10, 0),
                    Content = new TextBlock { Text = app.Muted ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 },
                    ToolTip = "Выключить звук программы",
                };
                mute.SetResourceReference(StyleProperty, "IconToggle");
                Grid.SetColumn(mute, 1);
                row.Children.Add(mute);

                var slider = new Slider { Value = app.Volume, VerticalAlignment = VerticalAlignment.Center };
                slider.SetResourceReference(StyleProperty, "VolumeSlider");
                Grid.SetColumn(slider, 2);
                row.Children.Add(slider);

                var value = new TextBlock { Text = $"{app.Volume}%", TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(value, 3);
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
                        sub.Text = process + (settings.AppVolumes.ContainsKey(process) ? " · запомнено" : "");
                        ScheduleSave();
                    }
                };
                mute.Click += (_, _) =>
                {
                    bool m = mute.IsChecked == true;
                    ((TextBlock)mute.Content).Text = m ? "" : "";
                    try { AppVolumeMixer.Set(deviceId, process, null, m); } catch { }
                };

                var card = new Border { Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(12), Child = row };
                card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
                MixerList.Children.Add(card);
            }
        }

        private void MixerRefresh_Click(object sender, RoutedEventArgs e) => RefreshMixer();

        public string? CurrentDeviceId => session?.Info.Id;

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

        public void ToggleOverlay()
        {
            if (overlay is { IsVisible: true }) { overlay.Close(); return; }
            overlay = new OverlayWindow(this);
            overlay.Closed += (_, _) => { overlay = null; if (PageMixer.IsVisible) RefreshMixer(); };
            overlay.Open();
        }

        // ===================== Discord =====================

        private void UpdateDiscord()
        {
            if (!settings.DiscordEnabled || string.IsNullOrWhiteSpace(settings.DiscordAppId))
            {
                if (discord != null) { _ = discord.ClearAsync(); discord.Dispose(); discord = null; }
                return;
            }
            discord ??= new DiscordPresence(settings.DiscordAppId!.Trim());
            string details = settings.ActiveCustom != null ? settings.ActiveCustom : "Режим: " + FactoryPresets.Name(settings.Mode);
            string state = session != null ? "🎧 " + ShortDeviceName() : "Наушники не подключены";
            var d = discord;
            _ = d.UpdateAsync(details, state).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DiscordStatus != null && settings.DiscordEnabled)
                    DiscordStatus.Text = d.Connected ? "Подключено к Discord ✓" : "Discord не запущен или Application ID неверный";
            })));
        }

        private string ShortDeviceName()
        {
            string name = session?.Info.Name ?? "";
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            return open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;
        }

        private void DiscordSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.DiscordEnabled = DiscordSwitch.IsChecked == true;
            DiscordSetup.Visibility = settings.DiscordEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (settings.DiscordEnabled && string.IsNullOrWhiteSpace(settings.DiscordAppId))
                DiscordStatus.Text = "Вставьте Application ID — и статус появится.";
            UpdateDiscord();
            ScheduleSave();
        }

        private void DiscordIdBox_LostFocus(object sender, RoutedEventArgs e)
        {
            string id = new string(DiscordIdBox.Text.Where(char.IsDigit).ToArray());
            DiscordIdBox.Text = id;
            if (id == (settings.DiscordAppId ?? "")) return;
            settings.DiscordAppId = id.Length > 0 ? id : null;
            if (discord != null) { discord.Dispose(); discord = null; }
            UpdateDiscord();
            ScheduleSave();
        }

    }
}
