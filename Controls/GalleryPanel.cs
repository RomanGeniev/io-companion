using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls
{
    public sealed class GalleryPanel : UserControl, IDisposable
    {
        private readonly Func<string> folder;
        private readonly TextBox search = new() { Width = 220, Height = 34, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock status = Text("Загружаем файлы…", 12, true), empty = Text("", 16), location = Text("", 11, true), pages = Text("", 12, true);
        private readonly UniformGrid cards = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
        private readonly ScrollViewer scroll;
        private readonly Button previous = Button("←"), next = Button("→");
        private readonly Button[] filters = { Button("Все"), Button("Видео"), Button("Снимки") };
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) }, debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly Dictionary<string, BitmapSource?> thumbnails = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<GalleryItem> items = Array.Empty<GalleryItem>();
        private CancellationTokenSource? scanCancel, previewCancel;
        private string currentFolder = "";
        private bool scanning, disposed, limited, missing;
        private int type, page;
        private const int PageSize = 24;

        public GalleryPanel(Func<string> folder)
        {
            this.folder = folder;
            var root = new Grid();
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new RowDefinition { Height = height });
            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var refresh = Button("Обновить"); DockPanel.SetDock(refresh, Dock.Right); toolbar.Children.Add(refresh); refresh.Click += async (_, _) => await RefreshAsync();
            var query = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 10, 0) };
            var label = Text("Поиск", 12, true); label.Margin = new Thickness(0, 0, 8, 0); query.Children.Add(label); query.Children.Add(search);
            DockPanel.SetDock(query, Dock.Right); toolbar.Children.Add(query);
            search.SetResourceReference(BackgroundProperty, "InsetBrush"); search.SetResourceReference(ForegroundProperty, "TextBrush"); search.SetResourceReference(BorderBrushProperty, "CardBorderBrush");
            AutomationProperties.SetName(search, "Поиск записей по имени, игре или монитору");
            search.ToolTip = "Имя файла, игра или номер монитора";
            var tabs = new StackPanel { Orientation = Orientation.Horizontal };
            for (int index = 0; index < filters.Length; index++)
            {
                int selected = index; filters[index].Margin = new Thickness(0, 0, 6, 0); tabs.Children.Add(filters[index]);
                filters[index].Click += (_, _) => { type = selected; page = 0; RenderItems(); };
            }
            toolbar.Children.Add(tabs); root.Children.Add(toolbar);
            status.Margin = new Thickness(0, 0, 0, 10); Grid.SetRow(status, 1); root.Children.Add(status);
            var body = new Grid(); scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            body.Children.Add(scroll); empty.TextAlignment = TextAlignment.Center; empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.Margin = new Thickness(24); empty.MaxWidth = 480; body.Children.Add(empty);
            Grid.SetRow(body, 2); root.Children.Add(body);
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal }; navigation.Children.Add(previous); pages.Margin = new Thickness(12, 0, 12, 0); navigation.Children.Add(pages); navigation.Children.Add(next);
            previous.ToolTip = "Предыдущие файлы"; next.ToolTip = "Следующие файлы";
            previous.Click += (_, _) => { page--; RenderItems(); }; next.Click += (_, _) => { page++; RenderItems(); };
            DockPanel.SetDock(navigation, Dock.Right); footer.Children.Add(navigation);
            var openFolder = Button("Папка записей"); openFolder.Margin = new Thickness(0, 0, 12, 0); openFolder.Click += (_, _) => OpenFolder();
            DockPanel.SetDock(openFolder, Dock.Left); footer.Children.Add(openFolder);
            location.TextTrimming = TextTrimming.CharacterEllipsis; location.TextWrapping = TextWrapping.NoWrap; footer.Children.Add(location);
            Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
            SizeChanged += (_, _) => cards.Columns = ActualWidth >= 1000 ? 4 : ActualWidth >= 680 ? 3 : 2;
            search.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
            debounce.Tick += (_, _) => { debounce.Stop(); page = 0; RenderItems(); };
            timer.Tick += async (_, _) => await RefreshAsync();
            Loaded += async (_, _) => { if (IsVisible && !disposed) { timer.Start(); await RefreshAsync(); } };
            IsVisibleChanged += async (_, _) =>
            {
                if (IsVisible && IsLoaded && !disposed) { timer.Start(); RenderItems(); await RefreshAsync(); }
                else if (!IsVisible) Pause();
            };
            Unloaded += (_, _) => Pause();
        }

        public async Task RefreshAsync()
        {
            if (disposed || scanning || !IsVisible) return;
            scanning = true;
            scanCancel?.Dispose(); var cancel = scanCancel = new CancellationTokenSource();
            try
            {
                string path = folder();
                var result = await Task.Run(() => GalleryService.Scan(path, cancel.Token), cancel.Token);
                if (cancel.IsCancellationRequested || disposed || !IsVisible) return;
                bool changed = currentFolder != path || !items.SequenceEqual(result.Items) || result.Limited != limited || result.Missing != missing;
                currentFolder = path; location.Text = path; location.ToolTip = path;
                limited = result.Limited; missing = result.Missing; items = result.Items;
                if (changed || cards.Children.Count == 0) RenderItems();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = "Не удалось прочитать папку. Проверьте доступ и нажмите «Обновить»."; SettingsStore.LogError(ex); }
            finally { scanning = false; }
        }

        private void RenderItems()
        {
            if (disposed) return;
            previewCancel?.Cancel(); previewCancel?.Dispose(); var cancel = previewCancel = new CancellationTokenSource();
            string query = search.Text.Trim();
            var filtered = items.Where(item => (type == 0 || item.Video == (type == 1)) && (query.Length == 0 || (item.Name + " " + item.Folder).Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
            int totalPages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize); page = Math.Clamp(page, 0, totalPages - 1);
            for (int index = 0; index < filters.Length; index++) filters[index].SetResourceReference(StyleProperty, index == type ? "AccentButton" : "GhostButton");
            previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < totalPages; pages.Text = $"{page + 1} / {totalPages}";
            status.Text = filtered.Length == 0 ? "Нет файлов для показа" : $"{page * PageSize + 1}–{Math.Min((page + 1) * PageSize, filtered.Length)} из {filtered.Length} · сначала новые" + (limited ? " · последние 500 файлов" : "");
            empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            empty.Text = query.Length > 0 || type != 0 ? "Ничего не найдено. Попробуйте другой фильтр или запрос." : "Здесь появятся ваши записи и снимки.\nСохраните повтор, завершите запись или сделайте скриншот.";
            if (missing && filtered.Length == 0 && query.Length == 0 && type == 0) status.Text = "Папка появится после первого сохранения";
            cards.Children.Clear(); scroll.ScrollToTop();
            var previews = new List<(GalleryItem Item, Image Image, TextBlock Placeholder)>();
            foreach (var item in filtered.Skip(page * PageSize).Take(PageSize)) cards.Children.Add(Card(item, previews));
            if (IsVisible) _ = LoadPreviews(previews, cancel.Token);
        }

        private FrameworkElement Card(GalleryItem item, List<(GalleryItem, Image, TextBlock)> previews)
        {
            var image = new Image { Stretch = Stretch.UniformToFill, Height = 112 };
            var fallback = Text(item.Video ? "▶" : "▧", 30, true); fallback.HorizontalAlignment = HorizontalAlignment.Center;
            var picture = new Grid { Height = 112, ClipToBounds = true }; picture.SetResourceReference(BackgroundProperty, "InsetBrush"); picture.Children.Add(image); picture.Children.Add(fallback);
            var badge = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Child = Text(item.Video ? "ВИДЕО" : "СНИМОК", 9, true) }; badge.SetResourceReference(Border.BackgroundProperty, "PopupBrush"); picture.Children.Add(badge);
            var preview = Button(""); preview.Content = picture; preview.Padding = new Thickness(0); preview.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            preview.ToolTip = "Открыть " + System.IO.Path.GetFileName(item.Path); AutomationProperties.SetName(preview, preview.ToolTip.ToString()); preview.Click += (_, _) => Open(item, false);
            var stack = new StackPanel(); stack.Children.Add(preview);
            var name = Text(item.Name, 12); name.FontWeight = FontWeights.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = item.Name; name.Margin = new Thickness(0, 8, 0, 3); stack.Children.Add(name);
            var context = Text(string.IsNullOrEmpty(item.Folder) ? "Папка записей" : item.Folder, 11, true); context.TextWrapping = TextWrapping.NoWrap; context.TextTrimming = TextTrimming.CharacterEllipsis; context.ToolTip = item.Folder; stack.Children.Add(context);
            var info = Text(item.Modified.ToLocalTime().ToString("dd.MM.yyyy · HH:mm") + " · " + Size(item.Bytes), 10, true); info.Margin = new Thickness(0, 5, 0, 8); stack.Children.Add(info);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
            var open = Button(item.Video ? "Смотреть" : "Открыть"); open.Margin = new Thickness(0, 0, 4, 0); open.Click += (_, _) => Open(item, false); actions.Children.Add(open);
            var reveal = Button("В папке"); reveal.Click += (_, _) => Open(item, true); Grid.SetColumn(reveal, 1); actions.Children.Add(reveal); stack.Children.Add(actions);
            var card = new Border { Child = stack, Padding = new Thickness(10), Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "CardBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            previews.Add((item, image, fallback)); return card;
        }

        private async Task LoadPreviews(List<(GalleryItem Item, Image Image, TextBlock Placeholder)> previews, CancellationToken token)
        {
            try
            {
                foreach (var entry in previews)
                {
                    token.ThrowIfCancellationRequested();
                    string key = entry.Item.Path + "|" + entry.Item.Modified.Ticks + "|" + entry.Item.Bytes;
                    if (!thumbnails.TryGetValue(key, out var bitmap))
                    {
                        bitmap = await GalleryService.ThumbnailAsync(entry.Item, token); token.ThrowIfCancellationRequested();
                        if (thumbnails.Count >= 72) thumbnails.Clear(); thumbnails[key] = bitmap;
                    }
                    entry.Image.Source = bitmap; entry.Placeholder.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SettingsStore.LogError(ex); }
        }

        private void Open(GalleryItem item, bool reveal)
        {
            try
            {
                if (!File.Exists(item.Path)) { status.Text = "Файл перемещён или удалён. Нажмите «Обновить»."; return; }
                if (reveal) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            catch (Exception ex) { status.Text = "Не удалось открыть файл: " + ex.Message; }
        }
        private void OpenFolder()
        {
            try { string path = folder(); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "Не удалось открыть папку: " + ex.Message; }
        }
        private void Pause() { timer.Stop(); debounce.Stop(); scanCancel?.Cancel(); previewCancel?.Cancel(); }
        public void Dispose() { disposed = true; Pause(); thumbnails.Clear(); cards.Children.Clear(); }
        private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} ГБ" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} МБ" : $"{Math.Max(1, bytes / 1024)} КБ";
        private static TextBlock Text(string text, double size, bool secondary = false)
        {
            var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            block.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "SubTextBrush" : "TextBrush"); return block;
        }
        private static Button Button(string text)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 6, 10, 6), MinHeight = 30 };
            button.SetResourceReference(StyleProperty, "GhostButton"); return button;
        }
    }
}
