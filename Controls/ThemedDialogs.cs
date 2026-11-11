using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls;

public class ThemedDialog : Window
{
    protected StackPanel Body { get; } = new() { Margin = new Thickness(22, 18, 22, 20) };
    protected ThemedDialog(Window? owner, string title, double width)
    {
        if (owner != null) Owner = owner;
        Title = title; Width = width; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        SetResourceReference(ForegroundProperty, "TextBrush");
        var surface = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = Body };
        surface.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        Content = surface;
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        caption.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(caption);
        var close = Button("✕", "GhostButton", () => DialogResult = false);
        close.Padding = new Thickness(8, 4, 8, 4); close.IsCancel = true;
        AutomationProperties.SetName(close, "Закрыть окно"); Grid.SetColumn(close, 1); header.Children.Add(close);
        Body.Children.Add(header);
    }
    protected static Button Button(string text, string style, Action action)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 8, 14, 8) };
        b.SetResourceReference(StyleProperty, style); b.Click += (_, _) => action(); return b;
    }
    protected void Actions(Button accept)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = Button("Отмена", "GhostButton", () => DialogResult = false);
        cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0);
        row.Children.Add(cancel); row.Children.Add(accept); Body.Children.Add(row);
        Loaded += (_, _) => cancel.Focus();
    }
}

public sealed class ConfirmPresetWindow : ThemedDialog
{
    public ConfirmPresetWindow(Window? owner, string name) : base(owner, "Удалить пресет?", 440)
    {
        Body.Children.Add(new TextBlock { Text = "«" + name + "»", FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = "Пресет исчезнет из списка сохранённых. Остальные пресеты останутся.", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); Body.Children.Add(hint);
        Actions(Button("Удалить", "AccentButton", () => DialogResult = true));
    }
}

public sealed class AccentColorWindow : ThemedDialog
{
    private readonly Grid square = new() { Height = 174, ClipToBounds = true };
    private readonly Ellipse marker = new() { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent };
    private readonly Canvas markerLayer = new() { IsHitTestVisible = false };
    private readonly Slider hueSlider = new() { Minimum = 0, Maximum = 360, SmallChange = 1, LargeChange = 15 };
    private readonly TextBox hex = new() { Width = 112, MaxLength = 7, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Border preview = new() { CornerRadius = new CornerRadius(8), Width = 76, Height = 34 };
    private readonly TextBlock error = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private double hue, saturation, value;
    private bool updating;
    public string SelectedHex { get; private set; } = "#000000";

    public AccentColorWindow(Window? owner, Color current) : base(owner, "Свой цвет оформления", 456)
    {
        square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        markerLayer.Children.Add(marker); square.Children.Add(markerLayer);
        square.MouseLeftButtonDown += (_, e) => { square.CaptureMouse(); Pick(e.GetPosition(square)); };
        square.MouseMove += (_, e) => { if (square.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Pick(e.GetPosition(square)); };
        square.MouseLeftButtonUp += (_, _) => square.ReleaseMouseCapture();
        square.SizeChanged += (_, _) => Refresh();
        HelpTips.Set(square, "Выберите оттенок на полосе ниже. В поле цвета слева направо растёт насыщенность, сверху вниз уменьшается яркость. Точный цвет можно ввести в поле HEX.");
        Body.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = square });
        var spectrum = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) spectrum.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        var hueRow = new Grid { Margin = new Thickness(0, 12, 0, 14), Height = 24 };
        hueRow.Children.Add(new Border { Height = 10, CornerRadius = new CornerRadius(5), Background = spectrum });
        hueSlider.SetResourceReference(StyleProperty, "VolumeSlider");
        hueSlider.Resources["TrackBrush"] = Brushes.Transparent; hueSlider.Resources["AccentGradient"] = Brushes.Transparent;
        AutomationProperties.SetName(hueSlider, "Оттенок цвета"); hueRow.Children.Add(hueSlider); Body.Children.Add(hueRow);
        hueSlider.ValueChanged += (_, _) => { if (!updating) { hue = hueSlider.Value; Refresh(); } };
        var swatches = new System.Windows.Controls.Primitives.UniformGrid { Columns = 12, Margin = new Thickness(-2, 0, -2, 14) };
        var swatchTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="Surface" CornerRadius="7" Padding="3" Background="Transparent" BorderBrush="{DynamicResource CardBorderBrush}" BorderThickness="1">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource TextBrush}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        foreach (string color in new[] { "#00B8D9", "#3B82F6", "#7C5CFC", "#A855F7", "#EC4899", "#F43F5E", "#F97316", "#EAB308", "#22C55E", "#14B8A6", "#FFFFFF", "#111827" })
        {
            var button = Button("", "GhostButton", () => Load((Color)ColorConverter.ConvertFromString(color)));
            button.Template = swatchTemplate;
            button.Content = new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), CornerRadius = new CornerRadius(6), Width = 22, Height = 22 };
            button.Padding = new Thickness(2); button.Margin = new Thickness(1); AutomationProperties.SetName(button, color); swatches.Children.Add(button);
        }
        Body.Children.Add(swatches);
        var entry = new Grid(); entry.ColumnDefinitions.Add(new ColumnDefinition()); entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); entry.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        entry.Children.Add(new TextBlock { Text = "Цвет HEX", VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(hex, 1); entry.Children.Add(hex); AutomationProperties.SetName(hex, "Цвет в формате #RRGGBB");
        preview.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(preview, 2); entry.Children.Add(preview); Body.Children.Add(entry);
        error.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush"); Body.Children.Add(error);
        hex.TextChanged += (_, _) => { if (!updating && ThemeManager.TryCustomColor(hex.Text, out var color)) { Load(color, false); error.Visibility = Visibility.Collapsed; } };
        var apply = Button("Применить", "AccentButton", () =>
        {
            if (!ThemeManager.TryCustomColor(hex.Text, out var color)) { error.Text = "Введите цвет в формате #RRGGBB."; error.Visibility = Visibility.Visible; hex.Focus(); return; }
            SelectedHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; DialogResult = true;
        });
        apply.IsDefault = true; Actions(apply); Load(current);
        Loaded += (_, _) => { hex.Focus(); hex.SelectAll(); };
    }
    private void Pick(Point p)
    {
        saturation = Math.Clamp(p.X / Math.Max(1, square.ActualWidth), 0, 1);
        value = 1 - Math.Clamp(p.Y / Math.Max(1, square.ActualHeight), 0, 1); Refresh();
    }
    private void Load(Color c, bool updateHex = true)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        value = max; saturation = max == 0 ? 0 : delta / max;
        if (delta > 0) hue = ((max == r ? (g - b) / delta : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4) * 60 + 360) % 360;
        Refresh(updateHex);
    }
    private void Refresh(bool updateHex = true)
    {
        updating = true;
        try
        {
            var c = FromHsv(hue, saturation, value); SelectedHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            square.Background = new SolidColorBrush(FromHsv(hue, 1, 1)); preview.Background = new SolidColorBrush(c);
            hueSlider.Value = hue; if (updateHex) hex.Text = SelectedHex;
            Canvas.SetLeft(marker, Math.Clamp(saturation * square.ActualWidth - 7, 0, Math.Max(0, square.ActualWidth - 14)));
            Canvas.SetTop(marker, Math.Clamp((1 - value) * square.ActualHeight - 7, 0, Math.Max(0, square.ActualHeight - 14)));
        }
        finally { updating = false; }
    }
    internal static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (h % 360) switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
