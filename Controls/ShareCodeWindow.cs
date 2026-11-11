using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GraphiteCompanion.Controls
{
    /// <summary>Small dialog: shows a preset code to copy, or asks for a code (and a name) to import.</summary>
    public sealed class ShareCodeWindow : Window
    {
        private readonly TextBox codeBox;
        private readonly TextBox? nameBox;
        private readonly TextBlock status;

        public string Code => codeBox.Text.Trim();
        public string PresetName => nameBox?.Text.Trim() ?? "";

        /// <param name="code">Code to show, or null to ask the user for one.</param>
        public ShareCodeWindow(Window owner, string? code, Func<string, string?>? validate = null)
        {
            Owner = owner;
            Title = code != null ? "Поделиться пресетом" : "Пресет по коду";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BgBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(new TextBlock { Text = Title, FontSize = 19, FontWeight = FontWeights.SemiBold });
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14),
                Text = code != null
                    ? "Отправьте этот код другу. Он нажмёт «Ввести код» в «Моих пресетах», вставит его — и получит вашу настройку эквалайзера."
                    : "Вставьте код, который вам прислали (например IO-1A2B-…). Пресет появится в «Моих пресетах».",
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            root.Children.Add(hint);

            codeBox = new TextBox
            {
                Text = code ?? "", IsReadOnly = code != null, FontSize = 16, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8, 6, 8, 6),
            };
            root.Children.Add(codeBox);

            if (code == null)
            {
                var nameLabel = new TextBlock { Text = "Название", FontSize = 12, Margin = new Thickness(2, 12, 0, 4) };
                nameLabel.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                root.Children.Add(nameLabel);
                nameBox = new TextBox { Text = "Пресет друга", MaxLength = 40, Padding = new Thickness(8, 6, 8, 6) };
                root.Children.Add(nameBox);
            }

            status = new TextBlock { Margin = new Thickness(2, 10, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            root.Children.Add(status);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancel = new Button { Content = code != null ? "Закрыть" : "Отмена", Margin = new Thickness(0, 0, 8, 0) };
            cancel.SetResourceReference(StyleProperty, "GhostButton");
            cancel.Click += (_, _) => { DialogResult = false; };
            var ok = new Button { Content = code != null ? "Скопировать" : "Добавить", MinWidth = 120 };
            ok.SetResourceReference(StyleProperty, "AccentButton");
            ok.Click += (_, _) =>
            {
                if (code != null)
                {
                    try { Clipboard.SetText(code); status.Text = "Скопировано в буфер обмена ✓"; }
                    catch { status.Text = "Не удалось скопировать — выделите код и нажмите Ctrl+C"; }
                    return;
                }
                string? error = validate?.Invoke(Code);
                if (error != null) { status.Text = error; return; }
                DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            root.Children.Add(buttons);
            Content = root;

            Loaded += (_, _) => { if (code == null) codeBox.Focus(); else codeBox.SelectAll(); };
        }
    }
}
