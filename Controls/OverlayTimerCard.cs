using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls
{
    public sealed class OverlayTimerCard : Border, IDisposable
    {
        private static readonly ControlTemplate AdjustTemplate = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RepeatButton">
              <Border x:Name="b" Background="Transparent" CornerRadius="5">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource GhostHoverBrush}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Background" Value="{DynamicResource GhostPressedBrush}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.25"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        private readonly OverlayTimer timer;
        private readonly TextBlock minutes = Digits(), seconds = Digits();
        private readonly TextBlock status = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Button start = new() { Width = 34, Height = 34, Margin = new Thickness(14, 0, 4, 0) };
        private readonly Button reset = new() { Width = 30, Height = 30 };
        private readonly RepeatButton minuteDown = AdjustButton("−"), minuteUp = AdjustButton("+");
        private readonly RepeatButton secondDown = AdjustButton("−"), secondUp = AdjustButton("+");
        private readonly Border progress = new() { CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Border track = new() { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 7, 0, 0), ClipToBounds = true };

        public OverlayTimerCard(OverlayTimer timer)
        {
            this.timer = timer;
            Width = 310;
            HorizontalAlignment = HorizontalAlignment.Right;
            CornerRadius = new CornerRadius(10);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 8, 10, 8);
            Margin = new Thickness(0, 10, 0, 0);
            SetResourceReference(BackgroundProperty, "InsetBrush");
            SetResourceReference(BorderBrushProperty, "CardBorderBrush");
            var heading = new TextBlock { Text = "Таймер", FontSize = 11 };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var header = new Grid { Children = { heading, status } };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            row.Children.Add(Part(minutes, "мин", minuteDown, minuteUp, 60));
            row.Children.Add(new Border { Width = 10 });
            row.Children.Add(Part(seconds, "сек", secondDown, secondUp, 1));
            start.SetResourceReference(StyleProperty, "RoundAccentButton");
            start.VerticalAlignment = reset.VerticalAlignment = VerticalAlignment.Center;
            start.Click += (_, _) => timer.StartOrPause();
            reset.SetResourceReference(StyleProperty, "RoundGhostButton");
            reset.Content = Icon("\uE777", 14);
            reset.Click += (_, _) => timer.Reset();
            HelpTips.Set(reset, "Сбросить таймер к выбранному времени.");
            AutomationProperties.SetName(reset, "Сбросить таймер");
            row.Children.Add(start); row.Children.Add(reset);
            track.SetResourceReference(BackgroundProperty, "GhostBrush");
            progress.SetResourceReference(BackgroundProperty, "AccentGradient");
            track.Child = progress;
            track.SizeChanged += (_, _) => RefreshProgress();
            Child = new StackPanel { Children = { header, row, track } };
            timer.Changed += Refresh;
            Refresh();
        }

        private FrameworkElement Part(TextBlock digits, string label, RepeatButton down, RepeatButton up, int step)
        {
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(down); controls.Children.Add(digits); controls.Children.Add(up);
            var caption = new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var part = new StackPanel { Children = { controls, caption } };
            down.Click += (_, _) => Change(-step);
            up.Click += (_, _) => Change(step);
            part.PreviewMouseWheel += (_, e) => { Change(e.Delta > 0 ? step : -step); e.Handled = true; };
            string unit = step == 60 ? "минуту" : "секунду";
            HelpTips.Set(down, $"Уменьшить на {unit}. Удерживайте кнопку для быстрого изменения.");
            HelpTips.Set(up, $"Увеличить на {unit}. Удерживайте кнопку для быстрого изменения.");
            HelpTips.Set(digits, $"Колёсико меняет время на {unit}. Диапазон таймера: от 1 секунды до 3 часов.");
            AutomationProperties.SetName(down, "Уменьшить на " + unit);
            AutomationProperties.SetName(up, "Увеличить на " + unit);
            return part;
        }

        private void Change(int delta)
        {
            if (delta == -60 && timer.Duration.TotalSeconds < 60) return;
            timer.SetSeconds((int)timer.Duration.TotalSeconds + delta);
        }

        private static TextBlock Digits()
        {
            var text = new TextBlock { Width = 44, TextAlignment = TextAlignment.Center, FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
            return text;
        }

        private static RepeatButton AdjustButton(string text)
        {
            var button = new RepeatButton { Content = text, Width = 20, Height = 28, FontSize = 15, Delay = 350, Interval = 80, Focusable = false, Template = AdjustTemplate, Cursor = System.Windows.Input.Cursors.Hand };
            button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return button;
        }

        private static TextBlock Icon(string glyph, double size) => new() { Text = glyph, FontSize = size, FontFamily = (FontFamily)Application.Current.FindResource("IconFont") };

        private void Refresh()
        {
            bool editable = timer.State is OverlayTimerState.Ready or OverlayTimerState.Finished;
            int value = (int)Math.Ceiling(timer.Remaining.TotalSeconds);
            minutes.Text = $"{value / 60:00}";
            seconds.Text = $"{value % 60:00}";
            status.Text = timer.State switch
            {
                OverlayTimerState.Running => "Идёт отсчёт",
                OverlayTimerState.Paused => "На паузе",
                OverlayTimerState.Finished => "Время вышло",
                _ => "",
            };
            status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            bool running = timer.State == OverlayTimerState.Running;
            string action = timer.State switch { OverlayTimerState.Running => "Пауза", OverlayTimerState.Paused => "Продолжить", OverlayTimerState.Finished => "Повторить", _ => "Запустить" };
            start.Content = Icon(running ? "\uE769" : "\uE768", 15);
            HelpTips.Set(start, action);
            AutomationProperties.SetName(start, action + " таймер");
            minuteDown.IsEnabled = editable && timer.Duration.TotalSeconds >= 60;
            secondDown.IsEnabled = editable && timer.Duration.TotalSeconds > 1;
            minuteUp.IsEnabled = secondUp.IsEnabled = editable && timer.Duration.TotalSeconds < 10800;
            reset.IsEnabled = timer.State != OverlayTimerState.Ready;
            RefreshProgress();
        }

        private void RefreshProgress() => progress.Width = Math.Max(0, track.ActualWidth * Math.Clamp(timer.Remaining.TotalSeconds / timer.Duration.TotalSeconds, 0, 1));
        public void Dispose() => timer.Changed -= Refresh;
    }
}
