using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GraphiteCompanion.Services;

namespace GraphiteCompanion
{
    public partial class MainWindow
    {
        private PushToTalkController? pushToTalk;
        private CheckBox? pttSwitch;
        private TextBlock? pttHint;
        private Button? pttKey;
        private bool pttHeld;

        private void RefreshPushToTalk()
        {
            if (PushToTalkHost == null) return;
            if (pttSwitch == null)
            {
                pttSwitch = new CheckBox { Content = "Нажми, чтобы говорить" };
                pttSwitch.SetResourceReference(StyleProperty, "Switch");
                pttSwitch.Click += (_, _) => SetPushToTalkEnabled(pttSwitch.IsChecked == true);
                pttHint = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
                pttHint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                pttKey = new Button { Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left };
                pttKey.SetResourceReference(StyleProperty, "GhostButton");
                pttKey.Click += (_, _) => OpenHotkeysWindow();
                var content = new StackPanel(); content.Children.Add(pttSwitch); content.Children.Add(pttHint); content.Children.Add(pttKey);
                var card = new Border { Child = content, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
                card.SetResourceReference(Border.BackgroundProperty, "CardBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                PushToTalkHost.Content = card;
            }
            bool available = HotkeyDefs.FirstOrDefault(d => d.Id == "ptt")?.Current != null;
            if (!settings.PushToTalk || !available || shutdownStarted) DetachPushToTalk();
            else if (pushToTalk == null && micSession?.HasVolume == true)
            {
                try
                {
                    var endpoint = micSession;
                    pushToTalk = new PushToTalkController(endpoint.GetMute, endpoint.SetMute);
                }
                catch (Exception ex) { SettingsStore.LogError(ex); settings.PushToTalk = false; ScheduleSave(); }
            }
            pttSwitch.IsChecked = settings.PushToTalk;
            pttKey!.Content = HotkeyLabel("ptt") + " · изменить";
            pttHint!.Text = !available ? "Клавиша занята. Назначьте свободное сочетание."
                : settings.PushToTalk ? (pushToTalk == null ? "Ожидаю выбранный микрофон." : pttHeld ? "Микрофон открыт — отпустите клавишу." : "Микрофон закрыт. Удерживайте клавишу для речи.")
                : "Открывает выбранный микрофон только на время удержания.";
            pttSwitch.ToolTip = "Работает для выбранного микрофона во всех приложениях. Кнопка микрофона возвращает обычное ручное управление. При выключении режима прежнее состояние микрофона восстановится.";
        }

        private void SetPushToTalkEnabled(bool enabled)
        {
            settings.PushToTalk = enabled;
            ApplyHotkeys();
            RefreshPushToTalk();
            ScheduleSave();
            PollMic();
        }
        private void SetPushToTalkHeld(bool held)
        {
            if (shutdownStarted || regionSelection != null) held = false;
            try { pushToTalk?.SetHeld(held); pttHeld = held && pushToTalk != null; }
            catch (Exception ex) { SettingsStore.LogError(ex); pttHeld = false; }
            RefreshPushToTalk();
            PollMic();
            overlay?.Refresh();
        }
        private void DetachPushToTalk()
        {
            try { pushToTalk?.Dispose(); }
            catch (Exception ex) { SettingsStore.LogError(ex); }
            finally { pushToTalk = null; pttHeld = false; }
        }
    }
}
