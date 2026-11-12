using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace GraphiteCompanion.Controls;

public sealed class ViewLayoutWindow : Window
{
    private readonly Dictionary<string, List<ViewBlockSetting>> draft = new(), original = new();
    private readonly IReadOnlyList<ViewLayoutGroup> groups;
    private readonly StackPanel rows = new();
    private bool saved;
    public ViewLayoutWindow(Window owner, string section, IReadOnlyList<ViewLayoutGroup> groups, Dictionary<string, List<ViewBlockSetting>> source, Action<Dictionary<string, List<ViewBlockSetting>>> save)
    {
        Owner = owner; this.groups = groups; Title = "Вид раздела «" + section + "»";
        Width = 360; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60); SizeToContent = SizeToContent.Height; ShowInTaskbar = false;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(BackgroundProperty, "CardBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        foreach (var group in groups) { source.TryGetValue(group.Id, out var items); draft[group.Id] = group.Normalize(items); original[group.Id] = group.Normalize(items); }
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Button("✕", Close); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var title = Text(Title, 18); title.FontWeight = FontWeights.SemiBold; title.MouseLeftButtonDown += (_, _) => DragMove(); header.Children.Add(title); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var hint = Text("Перетаскивайте блоки за ⠿ прямо в приложении. Здесь можно вернуть скрытое. Значения настроек сохраняются.", 12);
        hint.Margin = new Thickness(0, 0, 0, 14); hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var actions = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var apply = Button("Сохранить", () => { save(draft); saved = true; Close(); }); apply.SetResourceReference(StyleProperty, "AccentButton"); DockPanel.SetDock(apply, Dock.Right); actions.Children.Add(apply);
        var cancel = Button("Отмена", Close); DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
        var reset = Button("Исходный вид", () => { foreach (var group in groups) { draft[group.Id] = group.Normalize(null); group.Apply(null); } Render(); }); reset.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(reset); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); var surface = new Border { Child = root, BorderThickness = new Thickness(1) }; surface.SetResourceReference(Border.BackgroundProperty, "CardBrush"); surface.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush"); Content = surface;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => { foreach (var group in groups) { group.SetEditing(null); if (!saved) group.Apply(original[group.Id]); } };
        foreach (var group in groups) group.SetEditing(value => { draft[group.Id] = value; Render(); });
        Loaded += (_, _) => BlockDrag.PlaceBeside(this, owner);
        Render();
    }
    private void Render()
    {
        rows.Children.Clear();
        foreach (var group in groups)
        {
            var title = Text(group.Title, 13); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 10, 0, 7); rows.Children.Add(title);
            var values = draft[group.Id];
            foreach (var setting in values)
            {
                string name = group.Names[int.Parse(setting.Id)];
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                void Move(int delta) { int index = values.IndexOf(setting); int target = index + delta; if (target < 0 || target >= values.Count) return; (values[index], values[target]) = (values[target], values[index]); group.Apply(values); Render(); }
                var down = Button("↓", () => Move(1)); down.IsEnabled = values.IndexOf(setting) < values.Count - 1; AutomationProperties.SetName(down, "Ниже: " + name); DockPanel.SetDock(down, Dock.Right); row.Children.Add(down);
                var up = Button("↑", () => Move(-1)); up.IsEnabled = values.IndexOf(setting) > 0; AutomationProperties.SetName(up, "Выше: " + name); DockPanel.SetDock(up, Dock.Right); row.Children.Add(up);
                var visible = new CheckBox { Content = name, IsChecked = setting.Visible, VerticalAlignment = VerticalAlignment.Center };
                visible.SetResourceReference(StyleProperty, "Switch"); visible.Click += (_, _) => { setting.Visible = visible.IsChecked == true; group.Apply(values); }; row.Children.Add(visible); rows.Children.Add(row);
            }
        }
    }
    private static TextBlock Text(string value, double size) { var t = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return t; }
    private static Button Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(3, 0, 0, 0) }; b.SetResourceReference(StyleProperty, "GhostButton"); b.Click += (_, _) => action(); return b; }
}
