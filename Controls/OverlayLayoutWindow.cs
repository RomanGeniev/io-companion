using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphiteCompanion.Services;
using Microsoft.Win32;

namespace GraphiteCompanion.Controls;

public sealed class OverlayLayoutWindow : Window
{
    private OverlayLayout draft;
    private readonly OverlayLayout original;
    private readonly Action<OverlayLayout> preview, save;
    private readonly StackPanel rows = new();
    private readonly TextBox note = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 500, Height = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock imageName = Text("Картинка не выбрана");
    private readonly Slider opacity, scale, width;
    private readonly CheckBox footer = new() { Content = "Подсказка внизу" }, position = new() { Content = "Запоминать положение" };
    private bool loading, accepted;

    public OverlayLayoutWindow(Window owner, OverlayLayout value, Action<OverlayLayout> preview, Action<OverlayLayout> save)
    {
        Owner = owner; this.preview = preview; this.save = save; original = value.Copy(); draft = value.Copy();
        Title = "Настроить оверлей"; Width = 350; MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40); SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        SetResourceReference(BackgroundProperty, "CardBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var close = Button("✕", Close); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var title = Text("⠿  Оверлей"); title.Cursor = Cursors.SizeAll; title.FontSize = 18; title.FontWeight = FontWeights.SemiBold;  header.Children.Add(title); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var apply = Button("Сохранить", () => { save(draft.Copy().Normalize()); accepted = true; Close(); }); apply.SetResourceReference(StyleProperty, "AccentButton"); DockPanel.SetDock(apply, Dock.Right); actions.Children.Add(apply);
        var cancel = Button("Отмена", Close); DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
        var reset = Button("Исходный вид", () => { draft = new OverlayLayout(); LoadValues(); Changed(); }); reset.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(reset);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel();
        opacity = Range(body, "Прозрачность", 0, 65, 5, n => { draft.Opacity = 1 - n / 100; Changed(); }, "%");
        scale = Range(body, "Масштаб", 80, 125, 5, n => { draft.Scale = n / 100; Changed(); }, "%");
        width = Range(body, "Ширина", 820, 1200, 20, n => { draft.Width = n; Changed(); }, "");
        var checks = new WrapPanel { Margin = new Thickness(0, 4, 0, 12) };
        foreach (var check in new[] { footer, position }) { check.SetResourceReference(StyleProperty, "Switch"); check.Margin = new Thickness(0, 0, 18, 6); checks.Children.Add(check); }
        footer.Click += (_, _) => { draft.Footer = footer.IsChecked == true; Changed(); };
        position.Click += (_, _) => { draft.RememberPosition = position.IsChecked == true; Changed(); }; body.Children.Add(checks);
        var heading = Text("Блоки"); heading.FontSize = 16; heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 4, 0, 6); body.Children.Add(heading);
        var hint = Text("Тяните за ⠿ на самом оверлее.\nГалочки возвращают скрытые блоки."); hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); hint.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(hint); body.Children.Add(rows);
        var extras = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var custom = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        var expand = Button("Заметка и картинка ▾", () => custom.Visibility = custom.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible); extras.Children.Add(expand);
        note.SetResourceReference(BackgroundProperty, "InsetBrush"); note.SetResourceReference(ForegroundProperty, "TextBrush"); note.SetResourceReference(BorderBrushProperty, "CardBorderBrush");
        AutomationProperties.SetName(note, "Текст заметки"); note.TextChanged += (_, _) => { if (!loading) { draft.Note = note.Text; Changed(); } }; custom.Children.Add(note);
        var imageRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var choose = Button("Выбрать…", PickImage); DockPanel.SetDock(choose, Dock.Right); imageRow.Children.Add(choose);
        var clear = Button("Убрать", () => { draft.ImagePath = ""; imageName.Text = "Картинка не выбрана"; Changed(); }); DockPanel.SetDock(clear, Dock.Right); imageRow.Children.Add(clear);
        imageName.TextTrimming = TextTrimming.CharacterEllipsis; imageRow.Children.Add(imageName); custom.Children.Add(imageRow); extras.Children.Add(custom); body.Children.Add(extras);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); var surface = new Border { Child = root, BorderThickness = new Thickness(1) }; surface.SetResourceReference(Border.BackgroundProperty, "CardBrush"); surface.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush"); Content = surface;
        var collapse = Button("—", () => { body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; });
        collapse.ToolTip = "Свернуть / развернуть панель"; DockPanel.SetDock(collapse, Dock.Right); header.Children.Insert(1, collapse);
        header.Background = Brushes.Transparent; header.Cursor = Cursors.SizeAll;
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) => BlockDrag.PlaceBeside(this, owner);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) => { if (!accepted) preview(original.Copy()); };
        LoadValues();
    }

    public void UpdateFromCanvas(OverlayLayout value)
    {
        draft = value.Copy().Normalize(); LoadValues(); Changed();
    }
    private void LoadValues()
    {
        loading = true;
        opacity.Value = (1 - draft.Opacity) * 100; scale.Value = draft.Scale * 100; width.Value = draft.Width;
        footer.IsChecked = draft.Footer; position.IsChecked = draft.RememberPosition; note.Text = draft.Note;
        imageName.Text = string.IsNullOrEmpty(draft.ImagePath) ? "Картинка не выбрана" : Path.GetFileName(draft.ImagePath);
        RenderRows(); loading = false;
    }
    private void RenderRows()
    {
        rows.Children.Clear();
        foreach (var block in draft.Blocks)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            foreach (var w in new[] { new GridLength(1, GridUnitType.Star), new GridLength(120) }) row.ColumnDefinitions.Add(new() { Width = w });
            var check = new CheckBox { Content = OverlayLayout.Catalog.First(x => x.Id == block.Id).Name, IsChecked = block.Visible, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(StyleProperty, "Switch"); check.Click += (_, _) => { block.Visible = check.IsChecked == true; Changed(); }; row.Children.Add(check);
            var zone = new ComboBox { FontSize = 12, Height = 30, Margin = new Thickness(4, 0, 0, 0), ItemsSource = new[] { "Вся ширина", "Слева", "Справа" }, SelectedIndex = block.Zone };
            zone.SetResourceReference(StyleProperty, "LayoutCombo");
            AutomationProperties.SetName(zone, "Расположение: " + check.Content); zone.SelectionChanged += (_, _) => { block.Zone = zone.SelectedIndex; Changed(); }; Grid.SetColumn(zone, 1); row.Children.Add(zone);
            rows.Children.Add(row);
        }
    }
    private void PickImage()
    {
        var dialog = new OpenFileDialog { Title = "Картинка для оверлея", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1024; bitmap.StreamSource = stream; bitmap.EndInit();
            draft.ImagePath = dialog.FileName; draft.Blocks.First(x => x.Id == "image").Visible = true; imageName.Text = Path.GetFileName(dialog.FileName); RenderRows(); Changed();
        }
        catch (Exception) { imageName.Text = "Не удалось прочитать картинку"; }
    }
    private void Changed() { if (!loading) preview(draft.Copy().Normalize()); }
    private Slider Range(Panel parent, string label, double min, double max, double step, Action<double> change, string suffix)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = new GridLength(24) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(120) }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(60) });
        row.Children.Add(Text(label)); var value = Text(""); value.TextAlignment = TextAlignment.Right; Grid.SetColumn(value, 2); row.Children.Add(value);
        var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        slider.SetResourceReference(StyleProperty, "VolumeSlider"); AutomationProperties.SetName(slider, label); Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 3); row.Children.Add(slider);
        slider.ValueChanged += (_, _) => { value.Text = $"{slider.Value:0}{suffix}"; if (!loading) change(slider.Value); };
        value.Text = $"{slider.Value:0}{suffix}"; parent.Children.Add(row); return slider;
    }
    private static TextBlock Text(string value) { var t = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return t; }
    private static Button Button(string text, Action action) { var b = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2, 0, 0, 0) }; b.SetResourceReference(StyleProperty, "GhostButton"); b.Click += (_, _) => action(); return b; }
}
