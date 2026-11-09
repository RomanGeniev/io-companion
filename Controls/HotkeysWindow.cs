using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GraphiteCompanion.Controls
{
    /// <summary>Hotkeys list: click a combination, press the new keys. Esc cancels, the arrow resets one to default.</summary>
    public sealed class HotkeysWindow : Window
    {
        private readonly MainWindow main;
        private readonly StackPanel list = new();
        private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 18 };
        private HotkeyDef? recording;

        public HotkeysWindow(MainWindow main)
        {
            this.main = main;
            Owner = main;
            Title = "Горячие клавиши";
            Width = 600;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BgBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = "Горячие клавиши", FontSize = 19, FontWeight = FontWeights.SemiBold });
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
                Text = "Нажмите на сочетание и затем на новые клавиши. Они работают в любой программе и в играх. " +
                       "Панель по Win + G всегда открывается этой комбинацией, а выбранная здесь — дополнительная.",
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            head.Children.Add(hint);
            root.Children.Add(head);

            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition());
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.Children.Add(message);
            var resetAll = new Button { Content = "Сбросить все", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            resetAll.SetResourceReference(StyleProperty, "GhostButton");
            resetAll.Click += (_, _) => { main.ResetAllHotkeys(); Fill("Все клавиши возвращены к стандартным."); };
            Grid.SetColumn(resetAll, 1);
            bottom.Children.Add(resetAll);
            var done = new Button { Content = "Готово", MinWidth = 110, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, IsDefault = true };
            done.SetResourceReference(StyleProperty, "AccentButton");
            done.Click += (_, _) => Close();
            Grid.SetColumn(done, 2);
            bottom.Children.Add(done);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);
            Content = root;

            PreviewKeyDown += OnKey;
            Fill(null);
        }

        private void Fill(string? note)
        {
            recording = null;
            SetMessage(note, error: false);
            list.Children.Clear();
            string? group = null;
            foreach (var d in main.HotkeyDefs)
            {
                if (d.Group != group)
                {
                    group = d.Group;
                    var g = new TextBlock { Text = group.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 16, 0, 6) };
                    g.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
                    list.Children.Add(g);
                }
                list.Children.Add(Row(d));
            }
        }

        private Border Row(HotkeyDef d)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.Children.Add(new TextBlock { Text = d.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

            var key = new Button
            {
                Content = d.Current is { } c ? MainWindow.FormatHotkey(c.mods, c.key) : "не назначено",
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0),
            };
            key.SetResourceReference(StyleProperty, d.IsCustom ? "AccentButton" : "GhostButton");
            key.ToolTip = "Нажмите, затем новое сочетание";
            key.Click += (_, _) =>
            {
                Fill(null);                       // cancels a previous recording
                recording = d;
                var b = FindButton(d);
                if (b != null) b.Content = "Нажмите клавиши…  (Esc — отмена)";
                SetMessage("Нажмите новое сочетание, например Ctrl + Alt + K.", error: false);
            };
            buttons[d.Id] = key;
            Grid.SetColumn(key, 1);
            row.Children.Add(key);

            if (d.IsCustom)
            {
                var reset = new Button { Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont") }, ToolTip = "Вернуть стандартное сочетание", Margin = new Thickness(6, 0, 0, 0) };
                reset.SetResourceReference(StyleProperty, "GhostButton");
                reset.Click += (_, _) => { main.ResetHotkey(d); Fill(null); };
                Grid.SetColumn(reset, 2);
                row.Children.Add(reset);
            }

            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 6, 6, 6), Margin = new Thickness(0, 0, 0, 6), Child = row };
            box.SetResourceReference(Border.BackgroundProperty, "InsetBrush");
            return box;
        }

        private readonly Dictionary<string, Button> buttons = new();
        private Button? FindButton(HotkeyDef d) => buttons.TryGetValue(d.Id, out var b) ? b : null;

        private void SetMessage(string? text, bool error)
        {
            message.Text = text ?? "";
            message.SetResourceReference(TextBlock.ForegroundProperty, error ? "MutedFgBrush" : "SubTextBrush");
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (recording == null) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { Fill(null); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed) return; // wait for the real key
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            uint mods = (uint)Keyboard.Modifiers; // same bit values as the Win32 modifiers (Alt 1, Ctrl 2, Shift 4, Win 8)
            var def = recording;
            string? error = main.TrySetHotkey(def, mods, vk);
            if (error != null)
            {
                Fill(null);
                SetMessage(error, error: true);
                return;
            }
            Fill($"Готово: «{def.Label}» — {MainWindow.FormatHotkey(mods, vk)}.");
        }
    }
}
