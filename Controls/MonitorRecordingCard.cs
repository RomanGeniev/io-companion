using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GraphiteCompanion.Controls;

public sealed class MonitorRecordingCard : Border
{
    public sealed record Choice(int Value, string Label);
    private readonly StackPanel root = new();
    private readonly Button repeat, save, record, shot;
    private readonly TextBlock status;
    public event Action? RepeatRequested, SaveRequested, RecordRequested, ScreenshotRequested;
    public List<ComboBox> QualityControls { get; } = new();

    public MonitorRecordingCard(string title, string details)
    {
        CornerRadius = new CornerRadius(12); Padding = new Thickness(12); Margin = new Thickness(0, 0, 10, 10);
        BorderThickness = new Thickness(1); SetResourceReference(BackgroundProperty, "CardBrush"); SetResourceReference(BorderBrushProperty, "CardBorderBrush"); Child = root;
        root.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        var subtitle = new TextBlock { Text = details, FontSize = 11, TextWrapping = TextWrapping.Wrap, Height = 20, Margin = new Thickness(0, 4, 0, 6) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); root.Children.Add(subtitle);
        AutomationProperties.SetName(this, title);
        repeat = ActionButton("Включить повторы", "GhostButton", () => RepeatRequested?.Invoke());
        save = ActionButton("Сохранить повтор", "AccentButton", () => SaveRequested?.Invoke());
        record = ActionButton("Начать запись", "GhostButton", () => RecordRequested?.Invoke());
        shot = ActionButton("Скриншот", "GhostButton", () => ScreenshotRequested?.Invoke());
        status = new TextBlock { FontSize = 11, Height = 30, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
        HelpTips.Set(repeat, "Накапливает последние минуты только этого монитора. Длительность задаётся над карточками. Остановка повторов не останавливает обычную запись.");
        HelpTips.Set(save, "Сохраняет накопленный повтор только этого монитора. Пока буфер ещё пуст, кнопка недоступна.");
        HelpTips.Set(record, "Начинает или останавливает обычную запись только этого монитора. Остальные мониторы продолжают работать независимо.");
        HelpTips.Set(shot, "Сохраняет снимок только этого монитора.");
    }
    private static Button ActionButton(string text, string style, Action action)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 7, 8, 7) };
        b.SetResourceReference(StyleProperty, style); b.Click += (_, _) => action(); return b;
    }
    public void AddQuality(string label, IEnumerable<Choice> options, int selected, string help, Action<int> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(caption);
        var list = options.ToList(); var combo = new ComboBox { ItemsSource = list, DisplayMemberPath = "Label", FontSize = 12 };
        combo.SelectedItem = list.FirstOrDefault(o => o.Value == selected) ?? list[0];
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is Choice choice) changed(choice.Value); };
        AutomationProperties.SetName(combo, AutomationProperties.GetName(this) + ": " + label); HelpTips.Set(combo, help);
        Grid.SetColumn(combo, 1); row.Children.Add(combo); root.Children.Add(row); QualityControls.Add(combo);
    }
    public void Complete()
    {
        var repeatArea = new StackPanel(); repeatArea.Children.Add(repeat); repeatArea.Children.Add(status);
        var inset = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 10), Child = repeatArea };
        inset.SetResourceReference(BackgroundProperty, "InsetBrush"); root.Children.Add(inset);
        root.Children.Add(save);
        var actions = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        record.Margin = new Thickness(0, 0, 3, 0); shot.Margin = new Thickness(3, 0, 0, 0); Grid.SetColumn(shot, 1);
        actions.Children.Add(record); actions.Children.Add(shot); root.Children.Add(actions);
    }
    public void Update(bool repeating, bool receiving, int seconds, bool canSave, bool recording, bool recovering, bool busy, bool screenshotBusy, TimeSpan? elapsed = null)
    {
        repeat.Content = repeating ? "Остановить повторы" : "Включить повторы";
        status.Text = repeating ? receiving ? $"Накоплено {seconds / 60}:{seconds % 60:00}" : "Ожидание кадров…" : "Повторы выключены";
        record.Content = recording ? "Остановить" : "Начать запись";
        if (recording)
        {
            var time = elapsed ?? TimeSpan.Zero;
            status.Text += recovering ? " · восстановление записи" : $" · запись {(int)time.TotalMinutes}:{time.Seconds:00}";
        }
        repeat.IsEnabled = !busy; save.IsEnabled = canSave && !busy; record.IsEnabled = !busy; shot.IsEnabled = !screenshotBusy;
    }
}
