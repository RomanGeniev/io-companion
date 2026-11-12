using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using GraphiteCompanion.Services;
namespace GraphiteCompanion.Controls;
public sealed class GalleryRenameWindow : ThemedDialog
{
    private readonly TextBox input;
    public string NewName => input.Text;
    public GalleryRenameWindow(Window? owner, GalleryItem item) : base(owner, "Переименовать файл", 480)
    {
        input = new TextBox { Text = Path.GetFileNameWithoutExtension(item.Path), MaxLength = 180, Padding = new Thickness(10, 8, 10, 8) };
        input.SetResourceReference(BackgroundProperty, "InsetBrush"); input.SetResourceReference(ForegroundProperty, "TextBrush"); input.SetResourceReference(BorderBrushProperty, "CardBorderBrush"); AutomationProperties.SetName(input, "Имя файла"); Body.Children.Add(input);
        var hint = new TextBlock { Text = "Расширение " + Path.GetExtension(item.Path) + " сохранится. Новое имя будет видно и в папке.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) }; hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); Body.Children.Add(hint);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; Body.Children.Add(error);
        var apply = Button("Сохранить", "AccentButton", () => {
            try { string path = GalleryRename.Destination(item, input.Text); if (!path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) throw new IOException("Такое имя уже занято."); DialogResult = true; }
            catch (Exception ex) { error.Text = ex.Message; }
        }); apply.IsDefault = true; Actions(apply);
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}
