using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GraphiteCompanion.Audio;
using GraphiteCompanion.Models;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    // Spectrum analyzer, mic level meter, default-device switching, volume limit, hearing test.
    public partial class MainWindow
    {
        private readonly SpectrumAnalyzer analyzer = new();
        private readonly MicLevelMeter micMeter = new();
        private readonly float[] specLevels = new float[SpectrumAnalyzer.Bars];
        private readonly float[] specShown = new float[SpectrumAnalyzer.Bars];
        private readonly Path spectrumPath = new() { IsHitTestVisible = false };
        private DispatcherTimer? visualTimer;
        private bool hadDevice;
        private double micShownDb = -90;
        private DateTime lastLimitNotice = DateTime.MinValue;

        private void InitFeatures()
        {
            AnalyzerSwitch.IsChecked = settings.Analyzer;
            AutoDefaultSwitch.IsChecked = settings.AutoDefaultDevice;
            LimitSwitch.IsChecked = settings.VolumeLimitEnabled;
            LimitSlider.Value = settings.VolumeLimit;
            LimitSlider.IsEnabled = settings.VolumeLimitEnabled;
            LimitValueText.Text = $"{settings.VolumeLimit}%";

            spectrumPath.SetResourceReference(Shape.FillProperty, "SpectrumBrush");
            curveCanvas.Children.Insert(0, spectrumPath);

            visualTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            visualTimer.Tick += (_, _) => VisualTick();
            IsVisibleChanged += (_, _) => { UpdateMeters(); ScheduleTrim(); };
            StateChanged += (_, _) => { UpdateMeters(); ScheduleTrim(); };
            ScheduleTrim(); // started with --tray: trim once the startup work is done
            hadDevice = session != null;
            InitAppearance();
            InitUpdates();
            InitDriver();
            UpdateBrand();
            UpdateMeters();
        }

        private void DisposeFeatures()
        {
            visualTimer?.Stop();
            analyzer.Dispose();
            micMeter.Dispose();
        }

        private void UpdateBrand()
        {
            if (session == null) { BrandSubtitle.Text = "Наушники не подключены"; return; }
            string name = session.Info.Name;
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            string hw = open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;
            BrandSubtitle.Text = hw + (session.Eq != null ? " · " + session.Eq.Engine : "");
        }

        // ===================== Analyzer & mic meter =====================

        private DispatcherTimer? trimTimer;

        /// <summary>A few seconds after the window disappears into the tray, give its memory back to Windows.</summary>
        private void ScheduleTrim()
        {
            trimTimer ??= new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(4) };
            trimTimer.Stop();
            if (IsVisible && WindowState != WindowState.Minimized) return;
            trimTimer.Tick -= TrimTick;
            trimTimer.Tick += TrimTick;
            trimTimer.Start();
        }

        private void TrimTick(object? sender, EventArgs e)
        {
            trimTimer!.Stop();
            if (IsVisible && WindowState != WindowState.Minimized) return;
            spectrumPath.Data = null;
            MemoryTrimmer.Trim();
        }

        /// <summary>Hides / disables what the connected headset can't do instead of showing dead controls.</summary>
        private void UpdateCapabilityUi()
        {
            if (EqUnavailable == null) return;
            bool connected = session != null;
            bool hasEq = session?.Eq != null;
            EqUnavailable.Visibility = connected && !hasEq ? Visibility.Visible : Visibility.Collapsed;
            BandsGrid.Opacity = 1;
            bool eqUsable = !connected || hasEq;
            EqSwitch.IsEnabled = eqUsable;
            ClipGuardSwitch.IsEnabled = eqUsable;
            FlatButton.IsEnabled = eqUsable;
            HearingTestButton.IsEnabled = hasEq;
            if (!eqUsable) ResetButton.IsEnabled = false;

            bool anyEffect = outputCards.Any(c => c.Visibility == Visibility.Visible);
            bool noEffects = connected && (session!.Effects == null || !anyEffect);
            EffectsEmpty.Visibility = noEffects ? Visibility.Visible : Visibility.Collapsed;
            LinkEffectsSwitch.Visibility = noEffects ? Visibility.Collapsed : Visibility.Visible;
            ResetEffectsButton.Visibility = noEffects ? Visibility.Collapsed : Visibility.Visible;
            EffectsProfileText.Visibility = noEffects ? Visibility.Collapsed : Visibility.Visible;
            if (noEffects) foreach (var c in outputCards) c.Visibility = Visibility.Collapsed;
            UpdateDriverOffer();
            UpdateApoOffer();
        }

        /// <summary>Runs the analyzer / mic meter only while they are actually visible.</summary>
        private void UpdateMeters()
        {
            if (visualTimer == null) return;
            bool shown = IsVisible && WindowState != WindowState.Minimized;
            bool wantSpec = shown && settings.Analyzer && PageEq.Visibility == Visibility.Visible && session != null;
            bool wantMic = shown && PageMic.Visibility == Visibility.Visible && micSession != null;
            try
            {
                if (wantSpec && !analyzer.Running) analyzer.Start(session!.Info.Id);
                else if (!wantSpec && analyzer.Running) analyzer.Stop();
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            try
            {
                if (wantMic && !micMeter.Running) micMeter.Start(micSession!.Info.Id);
                else if (!wantMic && micMeter.Running) micMeter.Stop();
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }

            if (!analyzer.Running) { spectrumPath.Data = null; Array.Clear(specShown); }
            if (!micMeter.Running) { MicMeterFill.Width = 0; MicMeterText.Text = "—"; micShownDb = -90; }
            if (analyzer.Running || micMeter.Running) visualTimer.Start(); else visualTimer.Stop();
        }

        private void VisualTick()
        {
            if (analyzer.Running) DrawSpectrum();
            if (micMeter.Running)
            {
                double db = micMeter.TakePeakDb();
                micShownDb = Math.Max(db, micShownDb - 1.5); // fast attack, slow release
                double frac = Math.Clamp((micShownDb + 60) / 60, 0, 1);
                MicMeterFill.Width = frac * MicMeterTrack.ActualWidth;
                MicMeterText.Text = micShownDb <= -59 ? "тишина" : $"{micShownDb:0} дБ";
            }
        }

        private void DrawSpectrum()
        {
            double w = curveCanvas.ActualWidth, h = curveCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            analyzer.GetLevels(specLevels);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                double barW = w / specLevels.Length;
                for (int b = 0; b < specLevels.Length; b++)
                {
                    specShown[b] = Math.Max(specLevels[b], specShown[b] * 0.86f);
                    double bh = specShown[b] * h * 0.92;
                    if (bh < 1) continue;
                    double x = b * barW + 1.5, y = h - bh, bw = Math.Max(1, barW - 3);
                    ctx.BeginFigure(new Point(x, h), true, true);
                    ctx.LineTo(new Point(x, y), false, false);
                    ctx.LineTo(new Point(x + bw, y), false, false);
                    ctx.LineTo(new Point(x + bw, h), false, false);
                }
            }
            geo.Freeze();
            spectrumPath.Data = geo;
        }

        private void AnalyzerSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.Analyzer = AnalyzerSwitch.IsChecked == true;
            UpdateMeters();
            ScheduleSave();
        }

        // ===================== Default device =====================

        private void TrackConnection(AudioDeviceInfo? target)
        {
            if (initialized)
            {
                if (target != null && !hadDevice) OnHeadsetConnected();
                else if (target == null && hadDevice) OnHeadsetDisconnected();
            }
            hadDevice = target != null;
        }

        private void OnHeadsetConnected()
        {
            if (!settings.AutoDefaultDevice || session == null) return;
            bool changed = false;
            string? cur = DefaultDevice.Get(0);
            if (cur != session.Info.Id)
            {
                settings.PrevDefaultRender = cur;
                changed |= DefaultDevice.Set(session.Info.Id);
            }
            if (micSession != null)
            {
                string? curMic = DefaultDevice.Get(1);
                if (curMic != micSession.Info.Id)
                {
                    settings.PrevDefaultCapture = curMic;
                    changed |= DefaultDevice.Set(micSession.Info.Id);
                }
            }
            if (changed) NotifyChange("Звук переключён на наушники", "");
            ScheduleSave();
        }

        private void OnHeadsetDisconnected()
        {
            if (!settings.AutoDefaultDevice) return;
            try
            {
                var outputs = AudioDevices.ListActiveOutputs();
                var back = outputs.FirstOrDefault(d => d.Id == settings.PrevDefaultRender);
                if (back != null && DefaultDevice.Set(back.Id))
                    NotifyChange("Наушники отключены", "", "Звук: " + back.Name);
                var inputs = AudioDevices.ListActiveInputs();
                var backMic = inputs.FirstOrDefault(d => d.Id == settings.PrevDefaultCapture);
                if (backMic != null) DefaultDevice.Set(backMic.Id);
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            settings.PrevDefaultRender = null;
            settings.PrevDefaultCapture = null;
            ScheduleSave();
        }

        private void AutoDefaultSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.AutoDefaultDevice = AutoDefaultSwitch.IsChecked == true;
            if (settings.AutoDefaultDevice && session != null) OnHeadsetConnected(); // apply right away
            ScheduleSave();
        }

        // ===================== Volume limit =====================

        private int ClampToLimit(int percent) =>
            settings.VolumeLimitEnabled ? Math.Min(percent, settings.VolumeLimit) : percent;

        /// <summary>Called from polling: the volume wheel on the headset can't exceed the limit either.</summary>
        private void EnforceLimit(float currentVolume)
        {
            if (!settings.VolumeLimitEnabled || session == null) return;
            if (currentVolume * 100 <= settings.VolumeLimit + 0.5) return;
            try { session.SetVolume(settings.VolumeLimit / 100f); } catch { }
            if ((DateTime.Now - lastLimitNotice).TotalSeconds > 4)
            {
                lastLimitNotice = DateTime.Now;
                NotifyChange($"Громкость ограничена: {settings.VolumeLimit}%", "");
            }
        }

        private void LimitSwitch_Click(object sender, RoutedEventArgs e)
        {
            settings.VolumeLimitEnabled = LimitSwitch.IsChecked == true;
            LimitSlider.IsEnabled = settings.VolumeLimitEnabled;
            if (session != null) try { EnforceLimit(session.GetVolume()); } catch { }
            ScheduleSave();
        }

        private void LimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LimitValueText == null || !initialized) return;
            settings.VolumeLimit = (int)Math.Round(e.NewValue);
            LimitValueText.Text = $"{settings.VolumeLimit}%";
            if (session != null) try { EnforceLimit(session.GetVolume()); } catch { }
            ScheduleSave();
        }

        // ===================== Hearing test =====================

        private void HearingTest_Click(object sender, RoutedEventArgs e)
        {
            if (session?.Eq == null)
            {
                MessageBox.Show(this, "Для теста нужны наушники со встроенным эквалайзером C-Media.", Title);
                return;
            }
            var restore = BeginNeutralSound();
            int[]? result = null;
            try
            {
                var dlg = new HearingTestWindow(session.Info.Id) { Owner = this };
                if (dlg.ShowDialog() == true) result = dlg.Result;
            }
            finally
            {
                EndNeutralSound(restore);
            }
            if (result == null) return;

            const string name = "Мой слух";
            settings.Custom.RemoveAll(c => c.Name == name);
            settings.Custom.Insert(0, new CustomPreset { Name = name, Bands = result });
            ForgetAutoState();
            settings.ActiveCustom = name;
            RefreshCustomList();
            LoadPresetToUi();
            ScheduleSave();
        }

        /// <summary>Turns the EQ and all output effects off for a clean test tone; returns what to restore.</summary>
        private List<(Guid format, uint pid)> BeginNeutralSound()
        {
            var restore = new List<(Guid, uint)>();
            try
            {
                session?.Eq?.SetEnabled(false);
                lastWrittenEnabled = false;
                if (session?.Effects is { } store)
                {
                    foreach (var def in Effects.Output)
                    {
                        if (CurrentEffect(def.Format, def.EnablePid) == 1)
                        {
                            store.Write(def.Format, def.EnablePid, 0);
                            restore.Add((def.Format, def.EnablePid));
                        }
                    }
                }
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            return restore;
        }

        private void EndNeutralSound(List<(Guid format, uint pid)> restore)
        {
            try
            {
                if (session?.Effects is { } store)
                    foreach (var (format, pid) in restore) store.Write(format, pid, 1);
                lastWrittenEnabled = null; // ApplyToDevice re-sends the EQ switch
                ScheduleApply();
            }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }
    }
}
