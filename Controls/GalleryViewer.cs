using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls;

public sealed class GalleryViewer : Window
{
    private readonly MediaElement media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform, Volume = 0.5 };
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock message = Text(""), time = Text("0:00 / 0:00");
    private readonly Button play;
    private readonly Slider seek = new() { Minimum = 0, Maximum = 1, IsEnabled = false, MinWidth = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool playing, opened, dragging, updating, closed;
    private bool started;
    private GalleryItem item;
    private readonly GalleryItem[] playlist;
    private readonly TextBlock caption = Text("");
    private readonly Button previous, next, maximize;
    private readonly Grid controls;
    private Rect restoreBounds;
    private bool fullScreen;
    private readonly List<Task> posterTasks = new();
    private CancellationTokenSource? posterCancel;
    public Task PendingPreview => Task.WhenAll(posterTasks.ToArray());
    private void QueuePoster(double position) { posterTasks.RemoveAll(x => x.IsCompleted); posterTasks.Add(LoadPoster(position)); }
    public string FilePath => item.Path;

    public GalleryViewer(Window? owner, GalleryItem item, IEnumerable<GalleryItem>? playlist = null)
    {
        this.item = item; this.playlist = (playlist ?? new[] { item }).Where(x => x.Video == item.Video).DistinctBy(x => x.Path).ToArray(); Title = item.Name + " — SoundShot"; Owner = owner;
        Width = Math.Min(1000, SystemParameters.WorkArea.Width); Height = Math.Min(720, SystemParameters.WorkArea.Height);
        MinWidth = 600; MinHeight = 400; WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });
        SetResourceReference(BackgroundProperty, "CardBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => { if (PresentationSource.FromVisual(this) is HwndSource source) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; };
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        var close = Button("✕", Close); close.ToolTip = "Закрыть (Esc)"; AutomationProperties.SetName(close, "Закрыть просмотр"); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        maximize = Button("⛶", ToggleFullScreen);
        AutomationProperties.SetName(maximize, "На весь экран");
        maximize.ToolTip = "Развернуть / восстановить"; DockPanel.SetDock(maximize, Dock.Right); header.Children.Add(maximize);
        var external = Button("Во внешнем плеере", () => { Pause(); try { Process.Start(new ProcessStartInfo(this.item.Path) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } });
        if (!item.Video) external.Content = "Во внешней программе";
        DockPanel.SetDock(external, Dock.Right); header.Children.Add(external);
        caption.Text = item.Name; caption.FontWeight = FontWeights.SemiBold; caption.TextTrimming = TextTrimming.CharacterEllipsis; caption.ToolTip = item.Path;
        caption.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) maximize.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); else if (!fullScreen) DragMove(); };
        header.Children.Add(caption); root.Children.Add(header);
        var viewport = new Grid { ClipToBounds = true }; viewport.SetResourceReference(BackgroundProperty, "InsetBrush");
        viewport.Children.Add(media); viewport.Children.Add(image);
        viewport.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) ToggleFullScreen(); else if (this.item.Video) Toggle(); e.Handled = true; };
        message.TextWrapping = TextWrapping.Wrap; message.TextAlignment = TextAlignment.Center; message.HorizontalAlignment = HorizontalAlignment.Center; message.Margin = new Thickness(28); message.MaxWidth = 560; viewport.Children.Add(message);
        Grid.SetRow(viewport, 1); root.Children.Add(viewport);
        controls = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        controls.RowDefinitions.Add(new() { Height = GridLength.Auto }); controls.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var timeline = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Visibility = item.Video ? Visibility.Visible : Visibility.Collapsed };
        time.Margin = new Thickness(12, 0, 0, 0); DockPanel.SetDock(time, Dock.Right); timeline.Children.Add(time);
        seek.SetResourceReference(StyleProperty, "VolumeSlider"); seek.IsMoveToPointEnabled = true; AutomationProperties.SetName(seek, "Позиция видео"); timeline.Children.Add(seek); controls.Children.Add(timeline);
        var transport = new DockPanel(); Grid.SetRow(transport, 1); controls.Children.Add(transport);
        var volume = new Slider { Minimum = 0, Maximum = 1, Value = .5, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Громкость", Visibility = item.Video ? Visibility.Visible : Visibility.Collapsed };
        volume.SetResourceReference(StyleProperty, "VolumeSlider"); AutomationProperties.SetName(volume, "Громкость видео"); volume.ValueChanged += (_, _) => media.Volume = volume.Value; DockPanel.SetDock(volume, Dock.Right); transport.Children.Add(volume);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        previous = Button("⏮", () => Navigate(-1)); previous.ToolTip = "Предыдущий файл (Ctrl+←)"; AutomationProperties.SetName(previous, "Предыдущий файл"); buttons.Children.Add(previous);
        play = Button("▶ Играть", Toggle); play.Visibility = item.Video ? Visibility.Visible : Visibility.Collapsed; buttons.Children.Add(play);
        var stop = Button("■ Стоп", Stop); stop.Visibility = play.Visibility; buttons.Children.Add(stop);
        next = Button("⏭", () => Navigate(1)); next.ToolTip = "Следующий файл (Ctrl+→)"; AutomationProperties.SetName(next, "Следующий файл"); buttons.Children.Add(next);
        transport.Children.Add(buttons);
        Grid.SetRow(controls, 2); root.Children.Add(controls); var surface = new Border { Child = root, BorderThickness = new Thickness(1) }; surface.SetResourceReference(Border.BackgroundProperty, "CardBrush"); surface.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush"); Content = surface;
        seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => dragging = true));
        seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { dragging = false; Seek(); }));
        seek.ValueChanged += (_, _) => { if (!updating && !dragging) Seek(); };
        media.MediaOpened += (_, _) =>
        {
            if (closed) return;
            opened = true; message.Text = ""; play.IsEnabled = true;
            seek.IsEnabled = media.NaturalDuration.HasTimeSpan && media.NaturalDuration.TimeSpan.TotalSeconds > 0;
            seek.Maximum = seek.IsEnabled ? media.NaturalDuration.TimeSpan.TotalSeconds : 1;
            if (playing) StartPlayback(); else media.Pause(); UpdateTime(); timer.Start();
        };
        media.MediaEnded += (_, _) => { Pause(); media.Position = TimeSpan.Zero; started = false; QueuePoster(0); UpdateTime(); };
        media.MediaFailed += (_, e) => { if (!closed) { ReleaseMedia(); ShowError("Не удалось воспроизвести видео. Попробуйте открыть его во внешнем плеере.\n" + e.ErrorException.Message); } };
        timer.Tick += (_, _) => UpdateTime();
        Loaded += (_, _) => LoadCurrent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (fullScreen) ToggleFullScreen(); else Close(); }
            else if (e.Key == Key.F || e.Key == Key.F11) ToggleFullScreen();
            else if (e.Key == Key.Space && this.item.Video) Toggle();
            else if (e.Key is Key.Left or Key.Right)
            {
                int direction = e.Key == Key.Left ? -1 : 1;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Navigate(direction);
                else if (opened) seek.Value = Math.Clamp(media.Position.TotalSeconds + direction * 5, 0, seek.Maximum);
            }
            else return;
            e.Handled = true;
        };
        Closed += (_, _) => { closed = true; posterCancel?.Cancel(); posterCancel?.Dispose(); ReleaseMedia(); image.Source = null; };
    }

    private void LoadCurrent()
    {
        posterCancel?.Cancel(); ReleaseMedia(); started = false; image.Source = null; image.Visibility = Visibility.Visible;
        Title = item.Name + " — SoundShot"; caption.Text = item.Name; caption.ToolTip = item.Path;
        int index = Array.FindIndex(playlist, x => x.Path == item.Path); previous.IsEnabled = index > 0; next.IsEnabled = index >= 0 && index < playlist.Length - 1;
        time.Text = "0:00 / 0:00"; updating = true; seek.Value = 0; updating = false;
        try
        {
            if (item.Video) { message.Text = "Открываем видео…"; play.IsEnabled = true; play.Content = "▶ Играть"; media.Source = new Uri(item.Path); media.Pause(); QueuePoster(0); }
            else
            {
                message.Text = "";
                using var stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); image.Source = bitmap;
            }
        }
        catch (Exception ex) { ShowError("Не удалось открыть файл.\n" + ex.Message); }
    }
    private void Navigate(int direction)
    {
        int index = Array.FindIndex(playlist, x => x.Path == item.Path) + direction;
        if (index < 0 || index >= playlist.Length) return;
        item = playlist[index]; LoadCurrent();
    }
    private void ToggleFullScreen()
    {
        if (!fullScreen)
        {
            restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
            var bounds = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            ResizeMode = ResizeMode.NoResize; WindowChrome.SetWindowChrome(this, null);
            Left = bounds.Left / dpi.DpiScaleX; Top = bounds.Top / dpi.DpiScaleY; Width = bounds.Width / dpi.DpiScaleX; Height = bounds.Height / dpi.DpiScaleY;
            fullScreen = true; maximize.Content = "❐"; maximize.ToolTip = "Вернуться в окно (Esc)";
        }
        else
        {
            fullScreen = false; Left = restoreBounds.Left; Top = restoreBounds.Top; Width = restoreBounds.Width; Height = restoreBounds.Height;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0) });
            maximize.Content = "⛶"; maximize.ToolTip = "На весь экран (F)";
        }
    }
    private void Toggle()
    {
        if (!item.Video) return;
        if (playing) Pause(); else { playing = true; play.Content = "Ⅱ Пауза"; if (opened) StartPlayback(); }
    }
    private void StartPlayback() { started = true; posterCancel?.Cancel(); image.Visibility = Visibility.Collapsed; message.Text = ""; media.Play(); }
    private void Stop() { Pause(); if (opened) { media.Position = TimeSpan.Zero; started = false; QueuePoster(0); UpdateTime(); } }
    private void Pause() { if (opened) media.Pause(); playing = false; play.Content = "▶ Играть"; }
    private void Seek() { if (opened && seek.IsEnabled) { media.Position = TimeSpan.FromSeconds(seek.Value); if (!started) QueuePoster(seek.Value); UpdateTime(); } }
    private async Task LoadPoster(double position)
    {
        posterCancel?.Cancel(); posterCancel?.Dispose(); var cancel = posterCancel = new CancellationTokenSource();
        try
        {
            var bitmap = await GalleryService.ThumbnailAsync(item, cancel.Token, position, 1280);
            if (closed || started || cancel.IsCancellationRequested) return;
            image.Source = bitmap; image.Visibility = Visibility.Visible;
            if (opened) message.Text = bitmap == null ? "Нажмите «Играть», чтобы посмотреть видео." : "";
        }
        catch (OperationCanceledException) { }
    }
    private void UpdateTime()
    {
        if (!opened) return;
        if (!dragging) { updating = true; seek.Value = media.Position.TotalSeconds; updating = false; }
        static string Stamp(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
        time.Text = Stamp(media.Position) + " / " + (media.NaturalDuration.HasTimeSpan ? Stamp(media.NaturalDuration.TimeSpan) : "—");
    }
    private void ReleaseMedia() { timer.Stop(); opened = playing = false; media.Close(); media.Source = null; play.IsEnabled = seek.IsEnabled = false; }
    private void ShowError(string text) { message.Text = text; message.SetResourceReference(ForegroundProperty, "TextBrush"); }
    private static TextBlock Text(string text) { var block = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }; block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); return block; }
    private static Button Button(string text, Action action) { var button = new Button { Content = text, Padding = new Thickness(10, 7, 10, 7) }; button.SetResourceReference(StyleProperty, "GhostButton"); button.Click += (_, _) => action(); return button; }
}

public sealed class ConfirmGalleryDeleteWindow : ThemedDialog
{
    public ConfirmGalleryDeleteWindow(Window? owner, GalleryItem item) : base(owner, "Переместить файл в корзину?", 460)
    {
        Body.Children.Add(new TextBlock { Text = Path.GetFileName(item.Path), FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var hint = new TextBlock { Text = "Файл исчезнет из галереи. Его можно будет восстановить из корзины Windows.", Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush"); Body.Children.Add(hint);
        Actions(Button("В корзину", "AccentButton", () => DialogResult = true));
    }
}
