using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GraphiteCompanion.Controls;

public sealed partial class OverlayWindow
{
    private TextBox noteInput = null!;
    private TextBlock imageName = null!;
    private bool syncingOptions;
    private FrameworkElement NoteOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(SnapBoard.Text("Текст заметки"));
        noteInput = new TextBox { MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(noteInput, "Текст заметки");
        noteInput.TextChanged += (_, _) => { if (syncingOptions || !IsLayoutEditing) return; layout.Note = noteInput.Text; ApplyAppearance(); };
        panel.Children.Add(noteInput); return panel;
    }
    private FrameworkElement ImageOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        imageName = SnapBoard.Text(""); panel.Children.Add(imageName);
        var buttons = new WrapPanel();
        buttons.Children.Add(SnapBoard.Button("Выбрать картинку…", () => {
            var picker = new OpenFileDialog { Title = "Картинка для оверлея", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            try {
                using var stream = File.OpenRead(picker.FileName);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1024; bitmap.StreamSource = stream; bitmap.EndInit();
                layout.ImagePath = picker.FileName; imageName.Text = Path.GetFileName(picker.FileName); ApplyAppearance();
            } catch (Exception) { imageName.Text = "Не удалось прочитать картинку. Выберите другой файл."; }
        }));
        buttons.Children.Add(SnapBoard.Button("Убрать картинку", () => { layout.ImagePath = ""; imageName.Text = "Картинка не выбрана"; ApplyAppearance(); }));
        panel.Children.Add(buttons); return panel;
    }
    private void RefreshCustomOptions()
    {
        syncingOptions = true; noteInput.Text = layout.Note;
        imageName.Text = string.IsNullOrEmpty(layout.ImagePath) ? "Картинка не выбрана" : Path.GetFileName(layout.ImagePath);
        syncingOptions = false;
    }
    private FrameworkElement AppearanceOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Range(string title, double value, double min, double max, double step, string unit, Action<double> change)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            row.RowDefinitions.Add(new()); row.RowDefinitions.Add(new());
            row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
            row.Children.Add(SnapBoard.Text(title));
            var output=SnapBoard.Text($"{value:0}{unit}");output.TextAlignment=TextAlignment.Right;Grid.SetColumn(output,1);row.Children.Add(output);
            var slider=new Slider { Minimum=min,Maximum=max,TickFrequency=step,IsSnapToTickEnabled=true,Value=value,Margin=new Thickness(0,10,0,0) };
            slider.SetResourceReference(StyleProperty,"VolumeSlider");AutomationProperties.SetName(slider,title);
            Grid.SetRow(slider,1);Grid.SetColumnSpan(slider,2);row.Children.Add(slider);
            slider.ValueChanged += (_, _) => { output.Text = $"{slider.Value:0}{unit}"; change(slider.Value); ApplyAppearance(); };
            panel.Children.Add(row);
        }
        Range("Прозрачность", (1-layout.Opacity)*100, 0, 65, 1, "%", v => layout.Opacity = 1-v/100);
        Range("Масштаб", layout.Scale*100, 80, 125, 5, "%", v => layout.Scale = v/100);
        Range("Ширина", layout.Width, 820, 1200, 20, " px", v => layout.Width = v);
        var checks = new StackPanel();
        void Check(string title, bool value, Action<bool> change) {
            var check = new CheckBox { Content = title, IsChecked = value, Margin = new Thickness(0, 0, 16, 6) }; check.SetResourceReference(StyleProperty, "Switch");
            check.Click += (_, _) => { change(check.IsChecked == true); ApplyAppearance(); }; checks.Children.Add(check);
        }
        Check("Подсказки внизу", layout.Footer, v => layout.Footer = v);
        Check("Запоминать положение", layout.RememberPosition, v => layout.RememberPosition = v);
        panel.Children.Add(checks); var hint=SnapBoard.Text("Прозрачность меняет только оверлей. Эту панель можно передвинуть за заголовок.");hint.FontSize=12;hint.Margin=new Thickness(0,8,0,0);hint.SetResourceReference(TextBlock.ForegroundProperty,"SubTextBrush");panel.Children.Add(hint);return panel;
    }
}
