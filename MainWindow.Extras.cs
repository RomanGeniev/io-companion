using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    public sealed record QuickState(
        string Preset, string Device, bool EqEnabled, ListeningMode? Mode,
        int Volume, bool Muted, bool MicMuted, bool HasMic, int? Battery);

    public sealed class TargetItem
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
    }

    // Automation, linked effects, tray flyout, OSD, microphone hotkey, tests, import/export.
    public partial class MainWindow
    {
        private readonly Dictionary<string, int> currentEffects = new();
        private readonly AudioTests tests = new();
        private ForegroundWatcher? watcher;
        private (ListeningMode mode, string? custom)? autoBackup;
        private string? autoTarget;
        private bool applyingAuto;
        private OsdWindow? osd;
        private TrayFlyout? flyout;
        private bool? lastMicMuted;

        private void InitExtras()
        {
            ClipGuardSwitch.IsChecked = settings.ClipGuard;
            LinkEffectsSwitch.IsChecked = settings.LinkEffects;
            AutoSwitchSwitch.IsChecked = settings.AutoSwitch;
            FullscreenSwitch.IsChecked = settings.FullscreenMovies;
            OsdSwitch.IsChecked = settings.ShowOsd;
            MicSoundsSwitch.IsChecked = settings.MicSounds;

            RefreshRuleTargets();
            RebuildRules();

            watcher = new ForegroundWatcher();
            watcher.Changed += OnForegroundChanged;
            watcher.Start();


            tray.FlyoutRequested += ToggleFlyout; // the panel is created on first use
            tray.MicMuteRequested += () => ToggleMicMute(fromHotkey: false);

            // keep the autostart entry pointing to the current exe location
            try { if (Autostart.IsEnabled) Autostart.Set(true); } catch { }
        }

        private void DisposeExtras()
        {
            watcher?.Dispose();
            tests.Dispose();
            osd?.Close();
            flyout?.Close();
            if (autoBackup is { } b)
            {
                // don't persist a mode that was only switched on automatically
                settings.Mode = b.mode;
                settings.ActiveCustom = b.custom;
            }
        }

        // ===================== Notifications / OSD =====================

        private string CurrentGlyph() =>
            settings.ActiveCustom != null ? ""
            : settings.Mode switch
            {
                ListeningMode.Games => "",
                ListeningMode.Music => "",
                _ => "",
            };

        private void NotifyChange(string text, string? glyph = null, string? sub = null)
        {
            if (!settings.Notifications) return;
            if (settings.ShowOsd)
            {
                osd ??= new OsdWindow();
                osd.ShowMessage(glyph ?? CurrentGlyph(), text, sub);
            }
            else if (!IsActive)
            {
                tray.Notify("SoundShot", text);
            }
        }

        // ===================== Linked effects =====================

        private string ProfileKey => settings.ActiveCustom != null ? "custom:" + settings.ActiveCustom : "mode:" + settings.Mode;

        private void OnOutputEffectChanged(Guid format, uint pid, int value)
        {
            currentEffects[Effects.Key(format, pid)] = value;
            QueueEffect(false, format, pid, value);
            if (!settings.LinkEffects) return;
            var snapshot = new Dictionary<string, int>(currentEffects);
            if (ActiveCustom is { } custom) custom.Effects = snapshot;
            else settings.ProfileEffects[ProfileKey] = snapshot;
            ScheduleSave();
        }

        private void CaptureEffectsFromDevice(ApoPropertyStore store)
        {
            foreach (var def in Effects.Output)
            {
                currentEffects[Effects.Key(def.Format, def.EnablePid)] =
                    store.Read(def.Format, def.EnablePid) ?? (def.DefaultEnabled ? 1 : 0);
                foreach (var p in def.Params)
                    currentEffects[Effects.Key(def.Format, p.Pid)] = store.Read(def.Format, p.Pid) ?? p.Default;
            }
        }

        private int? CurrentEffect(Guid format, uint pid) =>
            currentEffects.TryGetValue(Effects.Key(format, pid), out var v) ? v : null;

        /// <summary>When effects are linked, switching profile also switches the effect set.</summary>
        private void ApplyLinkedEffects()
        {
            EffectsProfileText.Text = settings.LinkEffects
                ? (settings.ActiveCustom != null
                    ? $"Эффекты сохраняются для пресета «{settings.ActiveCustom}»"
                    : $"Эффекты сохраняются для режима «{FactoryPresets.Name(settings.Mode)}»")
                : "Эффекты общие для всех режимов";

            if (!settings.LinkEffects || session?.Effects == null || currentEffects.Count == 0) return;

            Dictionary<string, int>? snapshot = settings.ActiveCustom != null
                ? ActiveCustom?.Effects
                : settings.ProfileEffects.TryGetValue(ProfileKey, out var s) ? s : Effects.ModeDefaults(settings.Mode);
            if (snapshot == null) return;

            ApplyEffectSnapshot(snapshot);
        }

        /// <summary>Sends only the effect values that differ from the current ones and refreshes the cards.</summary>
        private void ApplyEffectSnapshot(Dictionary<string, int> snapshot)
        {
            if (session?.Effects == null) return;
            foreach (var (key, value) in snapshot)
            {
                if (currentEffects.TryGetValue(key, out var cur) && cur == value) continue;
                currentEffects[key] = value;
                var (format, pid) = Effects.ParseKey(key);
                QueueEffect(false, format, pid, value);
            }
            foreach (var c in outputCards) c.Load(CurrentEffect);
        }

        // ===================== Share code =====================

        private void ShareCode_Click(object sender, RoutedEventArgs e)
        {
            var effects = currentEffects.Count > 0 ? new Dictionary<string, int>(currentEffects) : null;
            string code = PresetCode.Encode((int[])bands.Clone(), effects);
            new ShareCodeWindow(this, code).ShowDialog();
        }

        private void EnterCode_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ShareCodeWindow(this, null, c =>
                PresetCode.Decode(c) == null ? "Код не распознан — проверьте, нет ли опечатки." : null);
            if (dlg.ShowDialog() != true) return;
            var decoded = PresetCode.Decode(dlg.Code);
            if (decoded == null) return;

            string name = dlg.PresetName.Length > 0 ? dlg.PresetName : "Пресет друга";
            settings.Custom.RemoveAll(c => c.Name == name);
            settings.Custom.Add(new CustomPreset { Name = name, Bands = decoded.Value.bands, Effects = decoded.Value.effects });
            ForgetAutoState();
            settings.ActiveCustom = name;
            RefreshCustomList();
            LoadPresetToUi();
            if (decoded.Value.effects != null && !settings.LinkEffects) ApplyEffectSnapshot(decoded.Value.effects);
            ScheduleSave();
            NotifyChange("Пресет добавлен", "", name);
        }

        private void LinkEffectsSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.LinkEffects = LinkEffectsSwitch.IsChecked == true;
            if (settings.LinkEffects && ActiveCustom == null && !settings.ProfileEffects.ContainsKey(ProfileKey))
                settings.ProfileEffects[ProfileKey] = new Dictionary<string, int>(currentEffects); // keep what's on now
            ApplyLinkedEffects();
            ScheduleSave();
        }

        // ===================== Clip guard =====================

        private int[] AppliedBands()
        {
            var a = (int[])bands.Clone();
            int max = a.Max();
            if (!settings.ClipGuard || max <= 0) return a;
            for (int i = 0; i < a.Length; i++) a[i] = Math.Max(EqBands.Min, a[i] - max);
            return a;
        }

        private void UpdateClipInfo()
        {
            int max = bands.Max();
            string? text = null;
            if (max > 0 && settings.ClipGuard)
                text = $"Защита от перегруза: общий уровень −{max} дБ, чтобы звук не хрипел";
            else if (max >= 6)
                text = $"Полосы подняты до +{max} дБ — на громкой музыке возможен хрип. Включите защиту от перегруза.";
            ClipInfo.Text = text ?? "";
            ClipInfo.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ClipGuardSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.ClipGuard = ClipGuardSwitch.IsChecked == true;
            UpdateClipInfo();
            ScheduleApply();
            ScheduleSave();
        }

        // ===================== Automation =====================

        private string TargetLabel(string target) =>
            target.StartsWith("custom:") ? "Мой: " + target[7..]
            : Enum.TryParse<ListeningMode>(target.Replace("mode:", ""), out var m) ? FactoryPresets.Name(m) : target;

        private List<TargetItem> TargetItems()
        {
            var list = Enum.GetValues<ListeningMode>()
                .Select(m => new TargetItem { Label = FactoryPresets.Name(m), Value = "mode:" + m }).ToList();
            list.AddRange(settings.Custom.Select(c => new TargetItem { Label = "Мой: " + c.Name, Value = "custom:" + c.Name }));
            return list;
        }

        private void RefreshRuleTargets()
        {
            var items = TargetItems();
            RuleTarget.ItemsSource = items;
            RuleTarget.SelectedIndex = 0;
        }

        private void RebuildRules()
        {
            RulesPanel.Children.Clear();
            var items = TargetItems();
            foreach (var rule in settings.Rules.OrderBy(r => r.Target).ThenBy(r => r.Title != null).ThenBy(r => r.Display))
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(rule.Title) ? "" : "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    Foreground = (Brush)FindResource("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center,
                });
                var name = new TextBlock
                {
                    Text = rule.Display, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "Label" };
                combo.SelectedItem = items.FirstOrDefault(i => i.Value == rule.Target);
                if (combo.SelectedItem == null)
                {
                    // the custom preset was deleted - show the raw target
                    var missing = new TargetItem { Label = TargetLabel(rule.Target) + " (нет)", Value = rule.Target };
                    combo.ItemsSource = items.Append(missing).ToList();
                    combo.SelectedItem = missing;
                }
                var r = rule;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is TargetItem t) { r.Target = t.Value; ScheduleSave(); }
                };
                Grid.SetColumn(combo, 2);
                row.Children.Add(combo);

                var del = new Button
                {
                    Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Удалить правило",
                    Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont") },
                };
                del.Click += (_, _) => { settings.Rules.Remove(r); RebuildRules(); ScheduleSave(); };
                Grid.SetColumn(del, 4);

                bool hasOwn = r.Target.StartsWith("custom:Игра:");
                var profile = new Button
                {
                    Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = hasOwn
                        ? "Открыть эквалайзер с отдельным звуком этой программы и подстроить его"
                        : "Сделать для этой программы отдельный звук: копия текущих настроек, которую можно подстроить только под неё",
                    Content = new TextBlock { Text = hasOwn ? "Настроить звук" : "Отдельный звук…", FontSize = 12 },
                };
                profile.Click += (_, _) =>
                {
                    if (hasOwn) { OpenGameProfile(r); return; }
                    string app = string.IsNullOrEmpty(r.Title) ? r.Process : r.Title;
                    var answer = MessageBox.Show(this,
                        $"Сделать для «{app}» отдельный звук?\n\n" +
                        $"Я создам пресет «Игра: {app}» из текущих настроек эквалайзера и эффектов и включу его для этой программы. " +
                        "Остальные программы и ваши пресеты не изменятся.\n\n" +
                        "Потом нажмите «Настроить звук» в этой строке, чтобы подстроить его. Вернуть всё обратно можно, выбрав в списке другой пресет.",
                        "Отдельный звук для программы", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes) return;
                    MakeGameProfile(r);
                    RebuildRules();
                    NotifyChange("Отдельный звук создан", "", r.Target[7..]);
                };
                Grid.SetColumn(profile, 3);
                row.Children.Add(profile);
                row.Children.Add(del);

                RulesPanel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("InsetBrush"), CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(14, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Child = row,
                });
            }
        }

        private static readonly string[] Browsers =
        {
            "chrome", "msedge", "firefox", "opera", "opera_gx", "browser", "brave", "vivaldi", "yandex", "arc",
            "Telegram", "Kinopoisk", "Netflix", "ApplicationFrameHost",
        };

        private void OnForegroundChanged(string process, string title, bool fullscreen)
        {
            // order: rule for the program > full-screen video in a browser > window-title rule
            var rule = settings.Rules.FirstOrDefault(r => string.IsNullOrEmpty(r.Title) && r.Matches(process, title));
            bool fullscreenVideo = false;
            if (rule == null && settings.FullscreenMovies && fullscreen &&
                Browsers.Any(b => b.Equals(process, StringComparison.OrdinalIgnoreCase)))
            {
                rule = new AppRule { Process = process, Target = "mode:Movies" };
                fullscreenVideo = true;
            }
            rule ??= settings.Rules.FirstOrDefault(r => !string.IsNullOrEmpty(r.Title) && r.Matches(process, title));
            string shortTitle = title.Length > 60 ? title[..60] + "…" : title;
            ForegroundText.Text = $"Активная программа: {process}" +
                                  (shortTitle.Length > 0 ? $"  ·  «{shortTitle}»" : "") +
                                  (fullscreenVideo ? "  ·  на весь экран" : "") +
                                  (rule != null ? $"   →   {TargetLabel(rule.Target)}" : "");
            if (!settings.AutoSwitch || !initialized) return;

            if (rule != null)
            {
                if (rule.Target.StartsWith("custom:") && !settings.Custom.Any(c => c.Name == rule.Target[7..])) return;
                autoBackup ??= (settings.Mode, settings.ActiveCustom);
                if (autoTarget == rule.Target) return;
                autoTarget = rule.Target;
                string before = PresetTitleText();
                ApplyTarget(rule.Target);
                if (PresetTitleText() != before) NotifyChange(PresetTitleText(), sub: fullscreenVideo ? "Видео на весь экран" : "Автоматически · " + process);
            }
            else if (autoBackup is { } b)
            {
                autoBackup = null;
                autoTarget = null;
                string before2 = PresetTitleText();
                applyingAuto = true;
                settings.Mode = b.mode;
                settings.ActiveCustom = b.custom != null && settings.Custom.Any(c => c.Name == b.custom) ? b.custom : null;
                LoadPresetToUi();
                applyingAuto = false;
                if (PresetTitleText() != before2) NotifyChange(PresetTitleText(), sub: "Обычный режим");
            }
        }

        private void ApplyTarget(string target)
        {
            applyingAuto = true;
            try
            {
                if (target.StartsWith("custom:"))
                {
                    settings.ActiveCustom = target[7..];
                    LoadPresetToUi();
                }
                else if (Enum.TryParse<ListeningMode>(target.Replace("mode:", ""), out var m))
                {
                    SelectMode(m, notify: false);
                }
            }
            finally
            {
                applyingAuto = false;
            }
        }

        /// <summary>A manual choice wins over the automatic one until the next program switch.</summary>
        private void ForgetAutoState()
        {
            if (applyingAuto) return;
            autoBackup = null;
            autoTarget = null;
        }

        private void FullscreenSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.FullscreenMovies = FullscreenSwitch.IsChecked == true;
            ScheduleSave();
        }

        private void AutoSwitchSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.AutoSwitch = AutoSwitchSwitch.IsChecked == true;
            if (!settings.AutoSwitch && autoBackup is { } b)
            {
                autoBackup = null;
                autoTarget = null;
                settings.Mode = b.mode;
                settings.ActiveCustom = b.custom;
                LoadPresetToUi();
            }
            ScheduleSave();
        }

        private void RuleKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RuleProcess == null) return;
            RuleProcess.ToolTip = RuleKind.SelectedIndex == 1
                ? "Текст из заголовка окна, например: Apple Music, Phantom Blade, YouTube"
                : "Имя программы (например cs2) — или выберите из запущенных";
        }

        private void RuleProcess_DropDownOpened(object? sender, EventArgs e)
        {
            if (RuleKind.SelectedIndex == 1)
            {
                // offer titles of open windows
                var titles = Process.GetProcesses()
                    .Select(p => { try { return p.MainWindowTitle; } catch { return ""; } })
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct().OrderBy(t => t).ToList();
                string current = RuleProcess.Text;
                RuleProcess.ItemsSource = titles;
                RuleProcess.Text = current;
                return;
            }
            var names = Process.GetProcesses()
                .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId; } catch { return false; } })
                .Select(p => p.ProcessName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string text = RuleProcess.Text;
            RuleProcess.ItemsSource = names;
            RuleProcess.Text = text;
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            string name = RuleProcess.Text.Trim();
            bool byTitle = RuleKind.SelectedIndex == 1;
            if (!byTitle && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) return;
            string target = (RuleTarget.SelectedItem as TargetItem)?.Value ?? "mode:Games";
            var rule = byTitle ? new AppRule { Title = name, Target = target } : new AppRule { Process = name, Target = target };
            settings.Rules.RemoveAll(r => r.Display.Equals(rule.Display, StringComparison.OrdinalIgnoreCase));
            settings.Rules.Add(rule);
            RuleProcess.Text = "";
            RebuildRules();
            ScheduleSave();
        }

        private void AddApp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AddAppWindow(this, TargetItems().Select(t => (t.Label, t.Value)).ToList(), PresetTitleText());
            if (dlg.ShowDialog() != true || dlg.ProcessName.Length == 0) return;

            var rule = new AppRule { Process = dlg.ProcessName, Target = dlg.Target == AddAppWindow.OwnSound ? "mode:Games" : dlg.Target };
            settings.Rules.RemoveAll(r => r.Display.Equals(rule.Display, StringComparison.OrdinalIgnoreCase));
            settings.Rules.Add(rule);
            if (dlg.Target == AddAppWindow.OwnSound) MakeGameProfile(rule);
            RebuildRules();
            ScheduleSave();
            NotifyChange("Программа добавлена", "", $"{rule.Process} → {TargetLabel(rule.Target)}");
        }

        /// <summary>Opens the equalizer on the program's own preset so it can be tuned.</summary>
        private void OpenGameProfile(AppRule rule)
        {
            string name = rule.Target[7..];
            if (!settings.Custom.Any(c => c.Name == name)) return;
            ForgetAutoState();
            settings.ActiveCustom = name;
            LoadPresetToUi();
            ScheduleSave();
            TabEq.IsChecked = true;
            NotifyChange("Настройте звук под программу", "", $"Изменения сохраняются в «{name}»");
        }

        private void ResetRules_Click(object sender, RoutedEventArgs e)
        {
            settings.Rules = AppSettings.DefaultRules();
            RebuildRules();
            ScheduleSave();
        }

        // ===================== Microphone mute =====================

        public void ToggleMicMute(bool fromHotkey)
        {
            if (micSession == null || !micSession.HasVolume)
            {
                if (fromHotkey) NotifyChange("Микрофон не найден", "");
                return;
            }
            try
            {
                bool muted = !micSession.GetMute();
                micSession.SetMute(muted);
                UpdateMicMuteUi(muted);
                if (settings.MicSounds) { if (muted) Beeper.MicMuted(); else Beeper.MicUnmuted(); }
                if (fromHotkey || !IsActive)
                    NotifyChange(muted ? "Микрофон выключен" : "Микрофон включён", muted ? "" : "");
                flyout?.Refresh();
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
            }
        }

        private void SyncTrayMic(bool muted)
        {
            if (lastMicMuted == muted) return;
            lastMicMuted = muted;
            tray.SetMicMuted(muted);
        }

        // ===================== Tray flyout API =====================

        public QuickState GetQuickState()
        {
            int volume = 0;
            bool muted = false, micMuted = false;
            try
            {
                if (session?.HasVolume == true) { volume = (int)Math.Round(session.GetVolume() * 100); muted = session.GetMute(); }
                if (micSession?.HasVolume == true) micMuted = micSession.GetMute();
            }
            catch { }
            return new QuickState(
                PresetTitleText(),
                session?.Info.Name ?? "Наушники не подключены",
                settings.EqEnabled,
                settings.ActiveCustom == null ? settings.Mode : null,
                volume, muted, micMuted, micSession?.HasVolume == true,
                batteryLevel?.Percent);
        }

        public void ToggleFlyout() => (flyout ??= new TrayFlyout(this)).Toggle();

        public void SelectModeFromOutside(ListeningMode mode) => SelectMode(mode, notify: false);

        public void ToggleEqFromOutside() => ToggleEq(notify: false);

        public IReadOnlyList<string> CustomPresetNames => settings.Custom.Select(c => c.Name).ToList();
        public string? ActiveCustomName => settings.ActiveCustom;

        public void SelectCustomFromOutside(string name)
        {
            if (!settings.Custom.Any(c => c.Name == name)) return;
            ForgetAutoState();
            settings.ActiveCustom = name;
            LoadPresetToUi();
            ScheduleSave();
        }

        public void SetVolumeFromOutside(int percent)
        {
            try { session?.SetVolume(percent / 100f); } catch { }
            suppressVolume = true;
            VolumeSlider.Value = percent;
            suppressVolume = false;
        }

        public void ToggleMuteFromOutside() => MuteButton_Click(this, new RoutedEventArgs());

        // ===================== Tests =====================

        private void ChannelTest_Click(object sender, RoutedEventArgs e)
        {
            if (tests.ChannelTestRunning)
            {
                tests.StopChannelTest();
                SetChannelUi(null);
                return;
            }
            if (session == null) return;
            try
            {
                tests.StartChannelTest(session.Info.Id, name => Dispatcher.BeginInvoke(new Action(() => SetChannelUi(name))));
                SetChannelUi("подготовка…");
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                ChannelTestText.Text = "Не удалось запустить проверку: " + ex.Message;
            }
        }

        private void SetChannelUi(string? channel)
        {
            if (channel != null && !tests.ChannelTestRunning) channel = null; // late callback after Stop
            if (channel == null)
            {
                ChannelTestText.Text = "Короткий шум по очереди из каждого канала. С включённым 7.1 удобно проверить, откуда слышен звук.";
                ChannelTestButtonText.Text = "Запустить";
                ChannelTestIcon.Text = "";
            }
            else
            {
                ChannelTestText.Text = "Сейчас звучит:  " + channel;
                ChannelTestButtonText.Text = "Стоп";
                ChannelTestIcon.Text = "";
            }
        }

        private void MonitorSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorSwitch.IsChecked != true)
            {
                tests.StopMonitor();
                return;
            }
            if (micSession == null || session == null)
            {
                MonitorSwitch.IsChecked = false;
                return;
            }
            try
            {
                tests.StartMonitor(micSession.Info.Id, session.Info.Id);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                MonitorSwitch.IsChecked = false;
                MessageBox.Show("Не удалось включить прослушивание: " + ex.Message, "SoundShot");
            }
        }

        // ===================== Import / export =====================

        private sealed class PresetFile
        {
            public string Format { get; set; } = "graphite-companion-presets";
            public int Version { get; set; } = 1;
            public List<CustomPreset> Presets { get; set; } = new();
        }

        private void ExportPresets_Click(object sender, RoutedEventArgs e)
        {
            if (settings.Custom.Count == 0)
            {
                MessageBox.Show("Сначала сохраните хотя бы один свой пресет.", "SoundShot");
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "IO пресеты.json",
                Filter = "Пресеты SoundShot|*.json",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var file = new PresetFile { Presets = settings.Custom };
                File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось сохранить: " + ex.Message, "SoundShot");
            }
        }

        private void ImportPresets_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Пресеты SoundShot|*.json|Все файлы|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var file = JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(dlg.FileName));
                var presets = file?.Presets?.Where(p => !string.IsNullOrWhiteSpace(p.Name) && p.Bands?.Length == EqBands.Count).ToList();
                if (presets == null || presets.Count == 0)
                {
                    MessageBox.Show("В файле нет пресетов SoundShot.", "SoundShot");
                    return;
                }
                foreach (var p in presets)
                {
                    p.Bands = p.Bands.Select(v => Math.Clamp(v, EqBands.Min, EqBands.Max)).ToArray();
                    settings.Custom.RemoveAll(c => c.Name == p.Name);
                    settings.Custom.Add(p);
                }
                RefreshCustomList();
                ScheduleSave();
                MessageBox.Show($"Импортировано пресетов: {presets.Count}", "SoundShot");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось прочитать файл: " + ex.Message, "SoundShot");
            }
        }

        // ===================== Settings switches =====================

        private void OsdSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.ShowOsd = OsdSwitch.IsChecked == true;
            ScheduleSave();
            if (settings.ShowOsd) { osd ??= new OsdWindow(); osd.ShowMessage(CurrentGlyph(), PresetTitleText(), "Так выглядит подсказка"); }
        }

        private void MicSoundsSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.MicSounds = MicSoundsSwitch.IsChecked == true;
            ScheduleSave();
        }
    }
}
