using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls
{
    /// <summary>Search the AutoEq database and pick a headphone model.</summary>
    public sealed class AutoEqWindow : Window
    {
        private readonly TextBox search;
        private readonly ListBox results;
        private readonly TextBlock status;
        private readonly Button apply;
        private readonly DispatcherTimer debounce;
        private List<AutoEqEntry> all = new();

        public int[]? ResultBands { get; private set; }
        public string ResultName { get; private set; } = "";

        public AutoEqWindow(Window owner)
        {
            Owner = owner;
            Title = "Пресет под мои наушники";
            Width = 600;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "BgBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;

            var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            for (int i = 0; i < 5; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            root.Children.Add(new TextBlock { Text = "Пресет под мои наушники", FontSize = 19, FontWeight = FontWeights.SemiBold });
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
                Text = "Найдите свою модель — пресет выровняет её звук по измерениям из открытой базы AutoEq (около 5000 наушников). " +
                       "Результат добавится в «Мои пресеты».",
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            search = new TextBox { Padding = new Thickness(8, 6, 8, 6), FontSize = 14 };
            Grid.SetRow(search, 2);
            root.Children.Add(search);

            results = new ListBox { Margin = new Thickness(0, 10, 0, 0), DisplayMemberPath = "Display", BorderThickness = new Thickness(0) };
            results.SetResourceReference(BackgroundProperty, "CardBrush");
            results.SetResourceReference(ForegroundProperty, "TextBrush");
            results.SelectionChanged += (_, _) => apply!.IsEnabled = results.SelectedItem != null;
            results.MouseDoubleClick += async (_, _) => await Apply();
            Grid.SetRow(results, 3);
            root.Children.Add(results);

            var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            bottom.ColumnDefinitions.Add(new ColumnDefinition());
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap, Text = "Загружаю базу…" };
            status.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            bottom.Children.Add(status);
            apply = new Button { Content = "Применить", MinWidth = 130, IsEnabled = false };
            apply.SetResourceReference(StyleProperty, "AccentButton");
            apply.Click += async (_, _) => await Apply();
            Grid.SetColumn(apply, 1);
            bottom.Children.Add(apply);
            Grid.SetRow(bottom, 4);
            root.Children.Add(bottom);
            Content = root;

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            debounce.Tick += (_, _) => { debounce.Stop(); RunSearch(); };
            search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
            Loaded += async (_, _) =>
            {
                search.Focus();
                try
                {
                    all = await AutoEqService.LoadIndexAsync();
                    status.Text = $"В базе {all.Count} профилей. Начните вводить название, например «HyperX Cloud» или «AirPods».";
                    RunSearch();
                }
                catch (Exception ex)
                {
                    status.Text = "Не удалось загрузить базу (нужен интернет): " + ex.Message;
                }
            };
        }

        private void RunSearch()
        {
            var found = AutoEqService.Search(all, search.Text).ToList();
            results.ItemsSource = found;
            if (all.Count > 0 && search.Text.Trim().Length > 0)
                status.Text = found.Count == 0 ? "Ничего не найдено — попробуйте короче, например только модель." : $"Найдено: {found.Count}";
        }

        private async System.Threading.Tasks.Task Apply()
        {
            if (results.SelectedItem is not AutoEqEntry entry) return;
            apply.IsEnabled = false;
            status.Text = "Загружаю профиль…";
            try
            {
                ResultBands = await AutoEqService.LoadBandsAsync(entry);
                ResultName = "AutoEq: " + entry.Name;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                status.Text = "Не удалось загрузить профиль: " + ex.Message;
                apply.IsEnabled = true;
            }
        }
    }
}
