using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using GraphiteCompanion.Controls;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    public partial class MainWindow
    {
        private AutoClickerService? autoClicker;
        private ActivityIndicator? clickerIndicator;
        private void AutoClickerKeyDown() { if (!AutoClickerOptions.HoldToRun || !AutoClickerState.Running) ToggleAutoClicker(); }
        private void AutoClickerKeyUp() { if (AutoClickerOptions.HoldToRun) StopAutoClicker(); }
        private void RefreshClickerIndicator()
        {
            if (shutdownStarted || !AutoClickerState.Running || !AutoClickerOptions.ShowIndicator || overlay is { IsVisible: true } || regionSelection != null) { clickerIndicator?.Hide(); return; }
            clickerIndicator ??= new ActivityIndicator();
            clickerIndicator.Update(AutoClickerState, AutoClickerOptions.HoldToRun ? "Отпустите " + HotkeyLabel("autoclicker") : HotkeyLabel("autoclicker") + " / Esc — стоп");
        }
        private DispatcherTimer? autoClickerUiTimer;
        private readonly HashSet<object> invalidClickerEditors = new();
        private object? clickerKeyRecorder;
        public event Action? AutoClickerChanged;
        public bool IsAutoClickerKeyRecording => clickerKeyRecorder != null;
        public AutoClickerSettings AutoClickerOptions => settings.AutoClicker ??= new();
        public AutoClickerState AutoClickerState => autoClicker?.State ?? new(false, 0, false, default, null);

        private void InitAutoClicker()
        {
            settings.AutoClicker = AutoClickerOptions.Normalize();
            AutoClickerHost.Content = new AutoClickerPanel(this, compact: false);
            autoClickerUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            AutoClickerState? displayed = null;
            autoClickerUiTimer.Tick += (_, _) =>
            {
                RefreshClickerIndicator();
                var current = AutoClickerState;
                if (current.Running || current != displayed) { displayed = current; AutoClickerChanged?.Invoke(); }
            };
            autoClickerUiTimer.Start();
        }

        public void SetAutoClickerOptions(AutoClickerSettings options)
        {
            if (AutoClickerOptions == options) return;
            autoClicker?.Stop();
            settings.AutoClicker = options.Normalize();
            ScheduleSave();
            AutoClickerChanged?.Invoke();
        }
        public void SetAutoClickerEditorValid(object editor, bool valid)
        {
            if (valid) invalidClickerEditors.Remove(editor); else invalidClickerEditors.Add(editor);
        }
        public void ToggleAutoClicker()
        {
            if (shutdownStarted || regionSelection != null) return;
            if (AutoClickerState.Running) { StopAutoClicker(); return; }
            if (IsAutoClickerKeyRecording || hotkeysEditor is { IsVisible: true }) return;
            if (invalidClickerEditors.Count > 0)
            {
                NotifyChange("Проверьте настройки автокликера", "\uE962", "Исправьте выделенное значение перед запуском");
                return;
            }
            if (HotkeyDefs.FirstOrDefault(d => d.Id == "autoclicker")?.Current == null)
            {
                NotifyChange("Назначьте клавишу автокликера", "\uE962", "F6 занята другой программой. Выберите свободную клавишу.");
                return;
            }
            autoClicker ??= new AutoClickerService();
            autoClicker.Start(AutoClickerOptions);
            AutoClickerChanged?.Invoke();
        }
        public void StopAutoClicker() { autoClicker?.Stop(); AutoClickerChanged?.Invoke(); }

        public bool BeginAutoClickerKeyRecording(object recorder)
        {
            if (IsAutoClickerKeyRecording || hotkeysEditor is { IsVisible: true }) return false;
            StopAutoClicker();
            clickerKeyRecorder = recorder;
            hotkeys.Clear();
            Resolve(register: false);
            return true;
        }
        public string? SaveAutoClickerKey(object recorder, uint modifiers, uint key)
        {
            if (!ReferenceEquals(clickerKeyRecorder, recorder)) return "Нажмите на кнопку сочетания ещё раз.";
            var definition = HotkeyDefs.First(d => d.Id == "autoclicker");
            string? error = TrySetHotkey(definition, modifiers, key);
            if (error == null) EndAutoClickerKeyRecording(recorder);
            return error;
        }
        public void EndAutoClickerKeyRecording(object recorder)
        {
            if (!ReferenceEquals(clickerKeyRecorder, recorder)) return;
            clickerKeyRecorder = null;
            if (!shutdownStarted) ApplyHotkeys();
            AutoClickerChanged?.Invoke();
        }
        public void ShowAutoClickerPage()
        {
            ShowFromTray();
            TabAutoClicker.IsChecked = true;
        }
    }
}
