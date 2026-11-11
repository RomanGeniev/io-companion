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

    // Output effects, tray flyout, OSD, microphone hotkey, tests, import/export.
    public partial class MainWindow
    {
        private readonly Dictionary<string, int> currentEffects = new();
        private readonly AudioTests tests = new();
        private OsdWindow? osd;
        private TrayFlyout? flyout;
        private bool? lastMicMuted;

        private void InitExtras()
        {
            ClipGuardSwitch.IsChecked = settings.ClipGuard;
            OsdSwitch.IsChecked = settings.ShowOsd;
            MicSoundsSwitch.IsChecked = settings.MicSounds;



            tray.MicMuteRequested += () => ToggleMicMute(fromHotkey: false);

            // keep the autostart entry pointing to the current exe location
            try { if (Autostart.IsEnabled) Autostart.Set(true); } catch { }
        }

        private void DisposeExtras()
        {
            tests.Dispose();
            osd?.Close();
            flyout?.Close();

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
            if (shutdownStarted || !settings.Notifications) return;
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

        // ===================== Output effects =====================

        private void OnOutputEffectChanged(Guid format, uint pid, int value)
        {
            currentEffects[Effects.Key(format, pid)] = value;
            QueueEffect(false, format, pid, value);
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

        // ===================== Share code =====================

        private void ShareCode_Click(object sender, RoutedEventArgs e)
        {
            string code = PresetCode.Encode((int[])bands.Clone(), null);
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
            settings.Custom.Add(new CustomPreset { Name = name, Bands = decoded.Value.bands });
            settings.ActiveCustom = name;
            RefreshCustomList();
            LoadPresetToUi();
            ScheduleSave();
            NotifyChange("Пресет добавлен", "", name);
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
                if (settings.PushToTalk) SetPushToTalkEnabled(false);
                micSession.SetMute(muted);
                UpdateMicMuteUi(muted);
                SyncTrayMic(muted);
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
                ChannelTestText.Text = "Короткий звук по очереди из каждого канала";
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

        private string? MonitorOutputId() => session?.Info.Id ?? DefaultDevice.Get(Native.DataFlowRender);

        private void MonitorSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorSwitch.IsChecked != true)
            {
                tests.StopMonitor();
                return;
            }
            string? outputId = MonitorOutputId();
            if (micSession == null || outputId == null)
            {
                MonitorSwitch.IsChecked = false;
                return;
            }
            try
            {
                tests.StartMonitor(micSession.Info.Id, outputId);
            }
            catch (Exception ex)
            {
                SettingsStore.LogError(ex);
                tests.StopMonitor();
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
