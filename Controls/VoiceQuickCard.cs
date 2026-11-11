using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GraphiteCompanion.Controls
{
    public sealed class VoiceQuickCard : StackPanel
    {
        private readonly List<(Button Button, int Value)> buttons = new();
        private static readonly ControlTemplate Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="b" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="8" Padding="2,5">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.75"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="b" Property="Opacity" Value="0.55"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);

        public VoiceQuickCard(Action<int> select)
        {
            Margin = new Thickness(0, 10, 0, 0);
            var title = new TextBlock { Text = "ГОЛОС МИКРОФОНА", FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            Children.Add(title);
            var row = new UniformGrid { Columns = 5, Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
            var labels = new[] { "Обычный", "Монстр", "Мульт", "Мужской", "Женский" };
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i - 1;
                var content = new StackPanel();
                content.Children.Add(Face(value));
                content.Children.Add(new TextBlock { Text = labels[i], FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) });
                var button = new Button { Content = content, Template = Template, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, i == 4 ? 0 : 4, 0), Cursor = System.Windows.Input.Cursors.Hand };
                button.Click += (_, _) => select(value);
                AutomationProperties.SetName(button, "Голос: " + labels[i]);
                HelpTips.Set(button, value < 0 ? "Обычный голос: отключить Magic Voice. Остальная обработка микрофона сохраняется." : "Включить Magic Voice: " + labels[i] + ". Применяется к выбранному совместимому микрофону io / C-Media.");
                buttons.Add((button, value)); row.Children.Add(button);
            }
            Children.Add(row);
            Visibility = Visibility.Collapsed;
        }

        private static FrameworkElement Face(int value)
        {
            string drawing = value switch
            {
                -1 => "M12,4 C9,4 8,6 8,9 L8,14 C8,19 16,19 16,14 L16,9 C16,6 15,4 12,4 M5,13 C5,24 19,24 19,13 M12,21 L12,26 M8,26 L16,26",
                0 => "M5,10 L3,2 L10,7 M19,10 L21,2 L14,7 M4,12 C4,4 20,4 20,12 L20,18 C20,28 4,28 4,18 Z M7,13 L10,15 M17,13 L14,15 M8,20 L10,23 L12,20 L14,23 L16,20",
                1 => "M5,11 C-2,6 5,1 8,7 M19,11 C26,6 19,1 16,7 M4,14 C4,3 20,3 20,14 C20,29 4,29 4,14 Z M8,13 L8,16 M16,13 L16,16 M8,20 Q12,25 16,20 M10,21 L10,24 M14,21 L14,24",
                2 => "M5,12 L5,7 Q12,0 19,7 L19,17 Q18,26 12,26 Q6,26 5,17 Z M5,10 L11,7 L19,10 M8,14 L8,16 M16,14 L16,16 M8,21 Q12,23 16,21",
                _ => "M3,24 L3,12 C3,-1 21,-1 21,12 L21,24 M6,12 Q12,9 14,5 Q16,10 18,12 L18,18 Q12,30 6,18 Z M8,14 L8,16 M16,14 L16,16 M9,21 Q12,23 15,21 M3,24 L7,22 M21,24 L17,22",
            };
            var path = new Path { Data = Geometry.Parse(drawing), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 26, Height = 28, Stretch = Stretch.Uniform };
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            return path;
        }

        public void Update(OverlayVoiceState state)
        {
            Visibility = state.Available ? Visibility.Visible : Visibility.Collapsed;
            foreach (var (button, value) in buttons)
            {
                bool active = state.Selected == value;
                button.SetResourceReference(Button.BackgroundProperty, active ? "AccentGradient" : "InsetBrush");
                button.SetResourceReference(Button.ForegroundProperty, active ? "AccentTextBrush" : "SubTextBrush");
                button.SetResourceReference(Button.BorderBrushProperty, active ? "AccentBrush" : "CardBorderBrush");
                AutomationProperties.SetItemStatus(button, active ? "Выбран" : "Не выбран");
            }
        }
    }
}
