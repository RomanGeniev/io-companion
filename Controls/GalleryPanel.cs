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
        public event Action? RecordRequested;
        private readonly Func<string> folder;
        private readonly TextBox search = new() { Width = 220, Height = 34, Padding = new Thickness(10, 6, 10, 6), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock status = Text("Загружаем файлы…", 12, true), empty = Text("", 20), emptyDetails = Text("", 13, true), location = Text("", 11, true), pages = Text("", 12, true);
        private readonly StackPanel emptyState = new() { Visibility = Visibility.Collapsed, MaxWidth=440, Margin=new Thickness(24), HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        private readonly StackPanel navigation = new() { Orientation = Orientation.Horizontal };
        private readonly Button emptyAction = Button("К записи экрана");
        private readonly UniformGrid cards = new() { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
        private readonly ScrollViewer scroll;
        private readonly Button previous = Button("←"), next = Button("→");
        private readonly Button[] filters = { Button("Все"), Button("Видео"), Button("Снимки") };
        private readonly ComboBox monitor = Select("Монитор", 180), application = Select("Приложение", 220), sizeFilter = Select("Размер файла", 164), sort = Select("Сортировка", 174);
        private readonly Button more = Button("Ещё фильтры"), reset = Button("Сбросить");
        private readonly WrapPanel advanced = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 4) };
        private GalleryViewer? viewer;
        private bool updatingFilters, deleting;
        private int revision;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) }, debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly Dictionary<string, BitmapSource?> thumbnails = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Task> previewTasks = new();
        private IReadOnlyList<GalleryItem> items = Array.Empty<GalleryItem>();
        private CancellationTokenSource? scanCancel, previewCancel;
        private string currentFolder = "";
        private bool scanning, disposed, limited, missing;
        private int type, page;
        private GalleryItem[] visibleItems = Array.Empty<GalleryItem>();
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
            toolbar.Children.Add(tabs);
            var tools = new StackPanel(); tools.Children.Add(toolbar);
            var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            options.Children.Add(monitor); options.Children.Add(more); options.Children.Add(reset);
            monitor.Margin = more.Margin = new Thickness(0, 0, 8, 4); reset.Margin = new Thickness(0, 0, 0, 4);
            more.Click += (_, _) => advanced.Visibility = advanced.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            tools.Children.Add(options); tools.Children.Add(advanced); root.Children.Add(tools);
            foreach (var box in new[] { application, sizeFilter, sort }) { box.Margin = new Thickness(0, 0, 8, 6); advanced.Children.Add(box); }
            monitor.Items.Add("Все мониторы"); monitor.SelectedIndex = 0;
            application.Items.Add("Все приложения"); application.SelectedIndex = 0;
            foreach (string optionLabel in new[] { "Любой размер", "До 10 МБ", "10–100 МБ", "100 МБ–1 ГБ", "От 1 ГБ" }) sizeFilter.Items.Add(optionLabel);
            foreach (string optionLabel in new[] { "Сначала новые", "Сначала старые", "Сначала тяжёлые", "Сначала лёгкие" }) sort.Items.Add(optionLabel);
            sizeFilter.SelectedIndex = sort.SelectedIndex = 0;
            foreach (var box in new[] { monitor, application, sizeFilter, sort }) box.SelectionChanged += (_, _) => { if (!updatingFilters) { page = 0; RenderItems(); } };
            reset.Click += (_, _) => ResetFilters();
            status.Margin = new Thickness(0, 0, 0, 10); Grid.SetRow(status, 1); root.Children.Add(status);
            var body = new Grid(); scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            body.Children.Add(scroll);
            var emptyIcon=Text("\uEB9F",28);emptyIcon.SetResourceReference(TextBlock.FontFamilyProperty,"IconFont");emptyIcon.SetResourceReference(TextBlock.ForegroundProperty,"SubTextBrush");emptyIcon.HorizontalAlignment=HorizontalAlignment.Center;
            var badge=new Border{Width=56,Height=56,CornerRadius=new CornerRadius(16),Child=emptyIcon,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,16)};badge.SetResourceReference(Border.BackgroundProperty,"InsetBrush");emptyState.Children.Add(badge);
            empty.TextAlignment=TextAlignment.Center;empty.FontWeight=FontWeights.SemiBold;empty.Margin=new Thickness(0,0,0,8);emptyState.Children.Add(empty);
            emptyDetails.TextAlignment=TextAlignment.Center;emptyDetails.Margin=new Thickness(0,0,0,18);emptyState.Children.Add(emptyDetails);
            emptyAction.HorizontalAlignment=HorizontalAlignment.Center;emptyAction.SetResourceReference(StyleProperty,"AccentButton");emptyState.Children.Add(emptyAction);body.Children.Add(emptyState);
            emptyAction.Click+=(_,_)=>{if(HasContentFilter)ResetFilters();else RecordRequested?.Invoke();};
            Grid.SetRow(body, 2); root.Children.Add(body);
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            navigation.Children.Add(previous); pages.Margin = new Thickness(12, 0, 12, 0); navigation.Children.Add(pages); navigation.Children.Add(next);
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
            if (disposed || scanning || deleting || !IsVisible) return;
            scanning = true;
            int scanRevision = revision;
            scanCancel?.Dispose(); var cancel = scanCancel = new CancellationTokenSource();
            try
            {
                string path = folder();
                var result = await Task.Run(() => GalleryService.Scan(path, cancel.Token), cancel.Token);
                if (cancel.IsCancellationRequested || disposed || !IsVisible || scanRevision != revision) return;
                bool changed = currentFolder != path || !items.SequenceEqual(result.Items) || result.Limited != limited || result.Missing != missing;
                currentFolder = path; location.Text = path; location.ToolTip = path;
                limited = result.Limited; missing = result.Missing; items = result.Items;
                if (changed || cards.Children.Count == 0) { UpdateChoices(); RenderItems(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { ShowStatus("Не удалось прочитать папку. Проверьте доступ и нажмите «Обновить»."); SettingsStore.LogError(ex); }
            finally { scanning = false; }
        }

        private bool HasContentFilter => type!=0 || monitor.SelectedIndex>0 || application.SelectedIndex>0 || sizeFilter.SelectedIndex>0 || !string.IsNullOrWhiteSpace(search.Text);
        private void ResetFilters()
        {
            updatingFilters=true;
            try { monitor.SelectedIndex=application.SelectedIndex=sizeFilter.SelectedIndex=sort.SelectedIndex=0;search.Text="";type=page=0;debounce.Stop(); }
            finally { updatingFilters=false; }
            RenderItems();
        }
        private void ShowStatus(string message) { status.Text=message;status.Visibility=Visibility.Visible; }

        private void RenderItems()
        {
            if (disposed) return;
            previewCancel?.Cancel(); previewCancel?.Dispose(); var cancel = previewCancel = new CancellationTokenSource();
            string query = search.Text.Trim();
            var selected = items.Where(item => (type == 0 || item.Video == (type == 1))
                && (monitor.SelectedIndex <= 0 || item.Monitor == (string)monitor.SelectedItem)
                && (application.SelectedIndex <= 0 || item.Application == (string)application.SelectedItem)
                && MatchesSize(item.Bytes, sizeFilter.SelectedIndex)
                && (query.Length == 0 || (item.Name + " " + item.Folder).Contains(query, StringComparison.CurrentCultureIgnoreCase)));
            var filtered = (sort.SelectedIndex switch
            {
                1 => selected.OrderBy(item => item.Modified),
                2 => selected.OrderByDescending(item => item.Bytes),
                3 => selected.OrderBy(item => item.Bytes),
                _ => selected.OrderByDescending(item => item.Modified)
            }).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            visibleItems = filtered;
            int extra = (application.SelectedIndex > 0 ? 1 : 0) + (sizeFilter.SelectedIndex > 0 ? 1 : 0) + (sort.SelectedIndex > 0 ? 1 : 0);
            more.Content = extra == 0 ? "Ещё фильтры" : $"Ещё фильтры · {extra}";
            more.ToolTip = "Приложение, размер файла и порядок показа";
            reset.Visibility = extra > 0 || monitor.SelectedIndex > 0 || query.Length > 0 || type != 0 ? Visibility.Visible : Visibility.Collapsed;
            int totalPages = Math.Max(1, (filtered.Length + PageSize - 1) / PageSize); page = Math.Clamp(page, 0, totalPages - 1);
            for (int index = 0; index < filters.Length; index++) filters[index].SetResourceReference(StyleProperty, index == type ? "AccentButton" : "GhostButton");
            previous.IsEnabled = page > 0; next.IsEnabled = page + 1 < totalPages; pages.Text = $"{page + 1} / {totalPages}";
            navigation.Visibility=totalPages>1?Visibility.Visible:Visibility.Collapsed;
            status.Text = (filtered.Length == 0 ? "Нет файлов для показа" : $"{page * PageSize + 1}–{Math.Min((page + 1) * PageSize, filtered.Length)} из {filtered.Length} · {sort.SelectedItem.ToString()!.ToLowerInvariant()}") + (limited ? " · в последних 500 файлах" : "");
            empty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            emptyState.Visibility=empty.Visibility;status.Visibility=filtered.Length==0?Visibility.Collapsed:Visibility.Visible;
            empty.Text=HasContentFilter?"Ничего не найдено":"Пока нет записей";
            emptyDetails.Text=HasContentFilter?"Измените условия поиска или сбросьте фильтры.":"Сохранённые повторы, видео и снимки появятся здесь."+(missing?" Папка создастся после первого сохранения.":"");
            emptyAction.Content=HasContentFilter?"Сбросить фильтры":"К записи экрана";
            cards.Children.Clear(); scroll.ScrollToTop();
            var previews = new List<(GalleryItem Item, Image Image, TextBlock Placeholder)>();
            foreach (var item in filtered.Skip(page * PageSize).Take(PageSize)) cards.Children.Add(Card(item, previews));
            previewTasks.RemoveAll(task => task.IsCompleted);
            if (IsVisible) previewTasks.Add(LoadPreviews(previews, cancel.Token));
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
            var name = Text(item.Name, 12); name.FontWeight = FontWeights.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = item.Name; name.Margin = new Thickness(0, 8, 0, 3); var nameRow = new DockPanel(); var rename = Button("✎"); rename.ToolTip = "Переименовать"; AutomationProperties.SetName(rename, "Переименовать " + item.Name); rename.Padding = new Thickness(6, 2, 6, 2); rename.Click += async (_, _) => await RenameAsync(item); DockPanel.SetDock(rename, Dock.Right); nameRow.Children.Add(rename); nameRow.Children.Add(name); stack.Children.Add(nameRow);
            var context = Text(string.IsNullOrEmpty(item.Folder) ? "Папка записей" : item.Folder, 11, true); context.TextWrapping = TextWrapping.NoWrap; context.TextTrimming = TextTrimming.CharacterEllipsis; context.ToolTip = item.Folder; stack.Children.Add(context);
            var info = Text(item.Modified.ToLocalTime().ToString("dd.MM.yyyy · HH:mm") + " · " + Size(item.Bytes), 10, true); info.Margin = new Thickness(0, 5, 0, 8); stack.Children.Add(info);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var open = Button(item.Video ? "Смотреть" : "Открыть"); open.Margin = new Thickness(0, 0, 4, 0); open.Click += (_, _) => Open(item, false); actions.Children.Add(open);
            var reveal = Button("↗"); reveal.ToolTip = "Показать в папке"; AutomationProperties.SetName(reveal, "Показать в папке " + item.Name); reveal.Click += (_, _) => Open(item, true); Grid.SetColumn(reveal, 1); actions.Children.Add(reveal);
            var delete = Button("✕"); delete.ToolTip = "Переместить в корзину"; AutomationProperties.SetName(delete, "Удалить " + item.Name); delete.Margin = new Thickness(4, 0, 0, 0); delete.Click += async (_, _) => await DeleteAsync(item); Grid.SetColumn(delete, 2); actions.Children.Add(delete); stack.Children.Add(actions);
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
                if (!File.Exists(item.Path)) { ShowStatus("Файл перемещён или удалён. Нажмите «Обновить»."); return; }
                if (reveal) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.Path + "\"") { UseShellExecute = true });
                else
                {
                    viewer?.Close(); viewer = new GalleryViewer(Window.GetWindow(this), item, visibleItems);
                    var openedViewer = viewer; viewer.Closed += (_, _) => { previewTasks.Add(openedViewer.PendingPreview); if (ReferenceEquals(viewer, openedViewer)) viewer = null; }; viewer.Show();
                }
            }
            catch (Exception ex) { ShowStatus("Не удалось открыть файл: " + ex.Message); }
        }
        private void OpenFolder()
        {
            try { string path = folder(); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowStatus("Не удалось открыть папку: " + ex.Message); }
        }
        private async Task RenameAsync(GalleryItem item)
        {
            if (deleting || disposed) return;
            deleting = true; revision++; scanCancel?.Cancel();
            try
            {
                var dialog = new GalleryRenameWindow(Window.GetWindow(this), item);
                if (dialog.ShowDialog() != true) return;
                previewCancel?.Cancel();
                if (viewer?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true) viewer.Close();
                await Task.WhenAll(previewTasks.ToArray());
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (disposed) return;
                GalleryRename.Rename(folder(), item, dialog.NewName); thumbnails.Clear();
            }
            catch (Exception ex) { if (!disposed) RenderItems(); ShowStatus("Не удалось переименовать: " + ex.Message); return; }
            finally { deleting = false; }
            await RefreshAsync();
        }
        private async Task DeleteAsync(GalleryItem item)
        {
            if (deleting || disposed) return;
            deleting = true; revision++; scanCancel?.Cancel();
            try
            {
                if (new ConfirmGalleryDeleteWindow(Window.GetWindow(this), item).ShowDialog() != true) return;
                previewCancel?.Cancel();
                if (viewer?.FilePath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) == true) viewer.Close();
                await Task.WhenAll(previewTasks.ToArray());
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                if (disposed) return;
                GalleryRecycle.Delete(folder(), item);
                items = items.Where(entry => entry.Path != item.Path).ToArray(); thumbnails.Clear();
                UpdateChoices(); RenderItems(); ShowStatus("Файл перемещён в корзину Windows.");
            }
            catch (Exception ex) { SettingsStore.LogError(ex); if (!disposed) RenderItems(); ShowStatus("Не удалось удалить файл: " + ex.Message); }
            finally { deleting = false; }
            await RefreshAsync();
        }
        private void UpdateChoices()
        {
            updatingFilters = true;
            try
            {
                static void Fill(ComboBox box, string all, System.Collections.Generic.IEnumerable<string> values)
                {
                    string selected = box.SelectedItem as string ?? all;
                    var choices = (all == "Все мониторы" ? values.Distinct().OrderBy(value => value.Length).ThenBy(value => value, StringComparer.CurrentCultureIgnoreCase) : values.Distinct().OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)).ToList();
                    // Preserve an active condition even when its last file has disappeared.
                    if (selected != all && !choices.Contains(selected)) choices.Add(selected);
                    box.Items.Clear(); box.Items.Add(all); foreach (string value in choices) box.Items.Add(value); box.SelectedItem = selected;
                }
                Fill(monitor, "Все мониторы", items.Select(item => item.Monitor)); Fill(application, "Все приложения", items.Select(item => item.Application));
            }
            finally { updatingFilters = false; }
        }
        internal static bool MatchesSize(long bytes, int range) => range switch { 1 => bytes < 10L * 1024 * 1024, 2 => bytes >= 10L * 1024 * 1024 && bytes < 100L * 1024 * 1024, 3 => bytes >= 100L * 1024 * 1024 && bytes < 1024L * 1024 * 1024, 4 => bytes >= 1024L * 1024 * 1024, _ => true };
        private static ComboBox Select(string label, double width)
        {
            var box = new ComboBox { Width = width, Height = 34, ToolTip = label, MaxDropDownHeight = 300 };
            box.SetResourceReference(StyleProperty, "LayoutCombo"); AutomationProperties.SetName(box, label); return box;
        }
        private void Pause() { timer.Stop(); debounce.Stop(); scanCancel?.Cancel(); previewCancel?.Cancel(); viewer?.Close(); }
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
