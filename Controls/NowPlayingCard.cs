using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls
{
    /// <summary>
    /// Mini player for the overlay: cover, title / artist / app, a seek bar with elapsed and remaining time,
    /// previous / play-pause / next. Shows whatever Windows reports as the current media session.
    /// </summary>
    public sealed class NowPlayingCard : Border, IDisposable
    {
        private readonly MainWindow main;
        private readonly Func<System.Threading.Tasks.Task<NowPlaying?>> readSnapshot;
        private readonly Canvas backdropLayer = new()
        {
            IsHitTestVisible = false, Opacity = 0, CacheMode = new BitmapCache { RenderAtScale = 0.5 },
        };
        private readonly Image backdrop = new() { Stretch = Stretch.UniformToFill, Effect = new BlurEffect { Radius = 60, RenderingBias = RenderingBias.Performance } };
        private readonly Border coverBox = new() { Width = 112, Height = 112, CornerRadius = new CornerRadius(14) };
        private readonly Image coverImage = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock coverGlyph = new() { Text = "", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 };
        private readonly TextBlock appText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock titleText = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock artistText = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        public ViewLayoutGroup LayoutButtons { get; private set; } = null!;
        private readonly Slider seek = new() { Margin = new Thickness(-4, 10, -4, 0) };
        private readonly TextBlock elapsedText = new() { FontSize = 11 };
        private readonly TextBlock remainText = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Grid timeRow = new();
        private readonly Button prev = new(), play = new(), next = new();
        private readonly TextBlock playGlyph;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

        private NowPlaying? state;
        private DateTime stateTime;
        private string? shownKey;
        private ImageSource? shownCover;
        private bool firstRead = true;
        private int emptyReads;
        private bool busy, dragging, scrubbing, settingValue;
        private DateTime ignoreUntil;     // after a seek / skip the app needs a moment before it reports the new position
        private int tick;
        private int seekFails;           // apps whose system seek was ignored: they may still work via their own slider
        private CancellationTokenSource? seekRequest;
        private bool panelMoving;
        private bool disposed;

        private static T R<T>(string key) => (T)Application.Current.FindResource(key);

        /// <summary>Is the point (relative to the slider) over the thumb circle itself?
        /// Thumb.IsMouseOver is not usable here: it stays true while the thumb keeps the mouse
        /// captured after a drag, which made every track click look like "on the thumb".</summary>
        private static bool OnThumb(System.Windows.Controls.Slider slider, System.Windows.Controls.Primitives.Track track, Point p)
        {
            var thumb = track.Thumb;
            if (thumb == null || thumb.ActualWidth <= 0) return false;
            Point origin = thumb.TranslatePoint(new Point(0, 0), slider);   // thumb's top-left in slider coords
            return p.X >= origin.X - 4 && p.X <= origin.X + thumb.ActualWidth + 4
                && p.Y >= origin.Y - 4 && p.Y <= origin.Y + thumb.ActualHeight + 4;
        }

        private void SeekToPoint(Track track, Point point)
        {
            double thumbWidth = track.Thumb?.ActualWidth ?? 0;
            double travel = track.ActualWidth - thumbWidth;
            if (travel <= 0) return;
            // Absolute geometry only: Track.ValueFromPoint combines the current Value with
            // a cached thumb center, which is stale between mouse events and the next Arrange.
            double fraction = Math.Clamp((point.X - thumbWidth / 2) / travel, 0, 1);
            if (track.IsDirectionReversed) fraction = 1 - fraction;
            seek.Value = seek.Minimum + fraction * (seek.Maximum - seek.Minimum);
        }

        public NowPlayingCard(MainWindow main, Func<System.Threading.Tasks.Task<NowPlaying?>>? readSnapshot = null)
        {
            this.main = main;
            this.readSnapshot = readSnapshot ?? MediaService.GetAsync;
            CornerRadius = new CornerRadius(16);
            BorderThickness = new Thickness(1);
            SetResourceReference(BackgroundProperty, "InsetBrush");
            SetResourceReference(BorderBrushProperty, "CardBorderBrush");
            Margin = new Thickness(0, 16, 0, 0);

            // ---- cover
            coverGlyph.FontFamily = R<FontFamily>("IconFont");
            coverGlyph.Foreground = Brushes.White;
            coverBox.SetResourceReference(Border.BackgroundProperty, "AccentGradient");
            coverBox.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 2, Opacity = 0.45 };
            var coverGrid = new Grid { Children = { coverGlyph, coverImage } };
            coverGrid.SizeChanged += (_, e) => coverGrid.Clip = new RectangleGeometry(new Rect(e.NewSize), 14, 14);
            coverBox.Child = coverGrid;

            // ---- texts
            appText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            artistText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            elapsedText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            remainText.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(appText);
            texts.Children.Add(titleText);
            texts.Children.Add(artistText);

            // ---- buttons
            Setup(prev, "", 16, "RoundGhostButton");
            playGlyph = Setup(play, "", 20, "RoundAccentButton");
            Setup(next, "", 16, "RoundGhostButton");
            prev.Click += async (_, _) => { Skip(); await MediaService.Previous(); };
            next.Click += async (_, _) => { Skip(); await MediaService.Next(); };
            play.Click += async (_, _) =>
            {
                if (state != null) { state = With(state, CurrentPosition(), !state.Playing); stateTime = DateTime.Now; Show(); }  // instant feedback
                ignoreUntil = DateTime.Now.AddMilliseconds(700);
                await MediaService.PlayPause();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            buttons.Children.Add(prev);
            play.Margin = new Thickness(8, 0, 8, 0);
            buttons.Children.Add(play);
            buttons.Children.Add(next);

            var buttonHost = CanvasParts.Wrap(buttons); buttonHost.MaxWidth = 350;
            LayoutButtons = new ViewLayoutGroup("Overlay.musicButtons", "Кнопки плеера", buttonHost, "Предыдущий трек", "Пауза / пуск", "Следующий трек");

            // ---- seek bar
            // Some apps (Apple Music) publish a timeline but ignore the system seek command
            // and declare "no position control". MediaService.Seek falls back to their own
            // window slider, so dragging is allowed whenever a timeline exists; after two
            // ignored seeks in a row the bar gives up and becomes read-only.
            seek.SetResourceReference(StyleProperty, "VolumeSlider");
            // Our handler owns track clicks as well as scrubbing. Otherwise Slider's class
            // handler marks the event handled before we can remember to send the seek command.
            seek.IsMoveToPointEnabled = false;
            seek.Minimum = 0;
            seek.Maximum = 1;
            seek.SmallChange = 5;
            seek.LargeChange = 10;
            seek.Focusable = false;   // arrow keys would snap the thumb back on the next poll
            seek.PreviewMouseLeftButtonDown += (_, e0) =>
            {
                if (!CanDragSeek()) { e0.Handled = true; return; }   // read-only: no drag, no click-to-point
                // Click anywhere on the track = jump right there, and keep scrubbing while the
                // button is held. The built-in move-to-point handler is disabled above;
                // Thumb.IsMouseOver is unreliable
                // (it stays true while the thumb has the mouse captured after a drag), so hit-test
                // the thumb's actual geometry instead.
                var track = seek.Template.FindName("PART_Track", seek) as System.Windows.Controls.Primitives.Track;
                if (track?.Thumb != null && !OnThumb(seek, track, e0.GetPosition(seek)))
                {
                    dragging = scrubbing = true;
                    SeekToPoint(track, e0.GetPosition(track));
                    seek.CaptureMouse();   // keep scrubbing even if the cursor leaves the bar
                    e0.Handled = true;   // the Thumb must not start a drag from a track click
                }
                else dragging = true;    // on the thumb: the Thumb starts its own drag
            };
            seek.MouseMove += (_, e0) =>
            {
                // scrubbing after a track click: the bar follows the mouse until the button is released
                if (!scrubbing || e0.LeftButton != MouseButtonState.Pressed) return;
                if (seek.Template.FindName("PART_Track", seek) is not System.Windows.Controls.Primitives.Track track) return;
                SeekToPoint(track, e0.GetPosition(track));
            };
            seek.PreviewMouseLeftButtonUp += async (_, _) =>
            {
                if (!dragging) return;
                dragging = scrubbing = false;
                if (seek.IsMouseCaptured) seek.ReleaseMouseCapture();
                if (state == null) return;
                var to = TimeSpan.FromSeconds(seek.Value);
                string key = state.Key;
                state = With(state, to, state.Playing);
                stateTime = DateTime.Now;
                ignoreUntil = DateTime.Now.AddMilliseconds(1500);   // the UIA fallback path needs longer
                seekRequest?.Cancel();
                var request = seekRequest = new CancellationTokenSource();
                try
                {
                    bool applied = await MediaService.Seek(to, request.Token);
                    if (request != seekRequest || state?.Key != key) return;
                    if (applied) seekFails = 0;
                    else if (++seekFails >= 2) Show();
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                catch (Exception ex) { SettingsStore.LogError(ex); }
                finally
                {
                    if (seekRequest == request)
                    {
                        seekRequest = null;
                        ignoreUntil = DateTime.Now.AddMilliseconds(700);
                    }
                    request.Dispose();
                }
            };
            seek.ValueChanged += (_, e) => { if (!settingValue) ShowTimes(TimeSpan.FromSeconds(e.NewValue)); };

            timeRow.ColumnDefinitions.Add(new ColumnDefinition());
            timeRow.ColumnDefinitions.Add(new ColumnDefinition());
            timeRow.Children.Add(elapsedText);
            Grid.SetColumn(remainText, 1);
            timeRow.Children.Add(remainText);
            timeRow.Margin = new Thickness(0, 2, 0, 0);

            // ---- layout: cover | (texts + buttons) / (seek + times)
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(texts);
            Grid.SetColumn(buttons, 1);
            top.Children.Add(buttons);

            var info = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(top);
            info.Children.Add(seek);
            info.Children.Add(timeRow);

            var body = new Grid { Margin = new Thickness(16) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.Children.Add(coverBox);
            Grid.SetColumn(info, 1);
            body.Children.Add(info);

            // blurred cover behind everything, clipped to the rounded card
            // the picture sits on a Canvas so it never makes the card taller: it is sized to the card instead
            backdropLayer.Children.Add(backdrop);
            var root = new Grid { Children = { backdropLayer, body } };
            root.SizeChanged += (_, e) =>
            {
                root.Clip = new RectangleGeometry(new Rect(e.NewSize), 15, 15);
                backdrop.Width = e.NewSize.Width + 160;     // a bit larger so the blurred edges stay outside
                backdrop.Height = e.NewSize.Height + 160;
                Canvas.SetLeft(backdrop, -80);
                Canvas.SetTop(backdrop, -80);
            };
            Child = root;

            timer.Tick += async (_, _) => await Tick();
            Loaded += async (_, _) => { if (IsVisible) { timer.Start(); await Fetch(); } };
            Unloaded += (_, _) => { timer.Stop(); seekRequest?.Cancel(); };
            IsVisibleChanged += async (_, _) => { if (IsVisible && IsLoaded && !disposed) { timer.Start(); await Fetch(); } else timer.Stop(); };
            state = MediaService.Recent;
            stateTime = state == null ? DateTime.Now : MediaService.LatestAt.ToLocalTime();
            Show();
        }

        internal void SetPanelMoving(bool moving)
        {
            if (disposed) return;
            panelMoving = moving;
            if (moving) timer.Stop();
            else if (IsLoaded && IsVisible) timer.Start();
        }

        private TextBlock Setup(Button b, string glyph, double size, string style)
        {
            var t = new TextBlock { Text = glyph, FontFamily = R<FontFamily>("IconFont"), FontSize = size };
            b.Content = t;
            b.SetResourceReference(StyleProperty, style);
            return t;
        }

        /// <summary>The hotkey texts can change while the overlay is open (it is rebuilt on every open anyway).</summary>
        public void RefreshHints()
        {
            prev.ToolTip = "Предыдущий трек · " + main.HotkeyLabel("media.prev");
            next.ToolTip = "Следующий трек · " + main.HotkeyLabel("media.next");
            play.ToolTip = "Пауза / продолжить · " + main.HotkeyLabel("media.play");
        }

        /// <summary>Dragging is allowed when the app takes the system command, or once or twice
        /// for apps that don't (their own slider may still take it via UIA).</summary>
        private bool CanDragSeek() => state != null && state.Duration > TimeSpan.Zero
            && (state.CanSeek || seekFails < 2);

        private void Skip()
        {
            ignoreUntil = DateTime.Now.AddMilliseconds(500);
            if (state != null) { state = With(state, TimeSpan.Zero, state.Playing); stateTime = DateTime.Now; Show(); }
        }

        private async System.Threading.Tasks.Task Tick()
        {
            // ask Windows twice a second, interpolate the bar smoothly in between (10 fps)
            if (++tick % 5 == 0) await Fetch();
            else ShowPosition();
        }

        private async System.Threading.Tasks.Task Fetch()
        {
            if (disposed || busy || panelMoving) return;
            busy = true;
            try
            {
                var s = await readSnapshot();
                if (disposed || panelMoving) return;
                if (s == null && ++emptyReads < 3) return;
                if (s != null) emptyReads = 0;
                firstRead = false;
                if ((dragging || seekRequest != null || DateTime.Now < ignoreUntil) && s != null && state != null && s.Key == state.Key) return;
                if (s?.Key != state?.Key) seekRequest?.Cancel();
                if (s == null || state == null || s.Key != state.Key) seekFails = 0;   // new track / app: give seeking a fresh chance
                if (s != null && state != null && s.Key == state.Key && s.Playing == state.Playing)
                {
                    // Same track, same play state: the system re-reports the position only every second or
                    // two, and always a bit behind our extrapolation. Taking it verbatim snaps the bar
                    // backwards - the "twitch". Keep the extrapolated position and resync the clock, so the
                    // next tick counts elapsed time exactly once.
                    if (Math.Abs((s.Position - CurrentPosition()).TotalSeconds) < 3.0)
                        s = With(s, CurrentPosition(), s.Playing);
                    // else: real jump (seek from the player's own UI) - take the polled position
                }
                stateTime = DateTime.Now;
                state = s;
                Show();
            }
            finally { busy = false; }
        }

        private TimeSpan CurrentPosition()
        {
            if (state == null) return TimeSpan.Zero;
            var p = state.Position;
            if (state.Playing) p += DateTime.Now - stateTime;
            if (state.Duration > TimeSpan.Zero && p > state.Duration) p = state.Duration;
            return p;
        }

        private void Show()
        {
            if (disposed) return;
            var s = state;
            bool has = s != null && (s.Title.Length > 0 || s.Artist.Length > 0);
            appText.Text = has ? ("СЕЙЧАС ИГРАЕТ · " + s!.App.ToUpperInvariant()).TrimEnd(' ', '·') : "МУЗЫКА";
            titleText.Text = has ? (s!.Title.Length > 0 ? s.Title : "Без названия") : firstRead ? "Загружаем трек…" : "Нет данных о треке";
            artistText.Text = has ? s!.Artist : firstRead ? "Получаем данные плеера" : "Плеер не передал сведения в Windows. Медиакнопки остаются доступны.";
            artistText.Visibility = artistText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            playGlyph.Text = s?.Playing == true ? "" : "";
            prev.IsEnabled = s == null || s.CanPrevious;     // without a session the media keys are sent
            next.IsEnabled = s == null || s.CanNext;
            RefreshHints();

            string key = has ? s!.Key + (s.Cover != null ? "+c" : "") : "";
            if (key != shownKey || !ReferenceEquals(shownCover, s?.Cover))
            {
                shownKey = key;
                shownCover = s?.Cover;
                coverImage.Source = s?.Cover;
                coverGlyph.Visibility = s?.Cover == null ? Visibility.Visible : Visibility.Collapsed;
                backdrop.Source = s?.Cover;
                double from = backdropLayer.Opacity;
                double target = s?.Cover != null ? 0.32 : 0;
                backdropLayer.Opacity = target;
                backdropLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(400))
                    { FillBehavior = FillBehavior.Stop });
            }

            bool timeline = has && s!.Duration > TimeSpan.Zero;
            seek.Visibility = timeRow.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
            bool canSeek = timeline && CanDragSeek();
            seek.Opacity = timeline ? 1 : 0.45;
            seek.Cursor = canSeek ? Cursors.Hand : Cursors.No;   // No = "you can look, not touch"
            seek.ToolTip = timeline && !canSeek ? "Эта программа не даёт перематывать — только показывает прогресс" : null;
            ShowPosition();
        }

        private void ShowPosition()
        {
            if (state == null || state.Duration <= TimeSpan.Zero || dragging) return;
            var p = CurrentPosition();
            settingValue = true;
            seek.Maximum = Math.Max(1, state.Duration.TotalSeconds);
            seek.Value = p.TotalSeconds;
            settingValue = false;
            ShowTimes(p);
        }

        private void ShowTimes(TimeSpan p)
        {
            if (state == null) return;
            var left = state.Duration - p;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            elapsedText.Text = MediaService.Format(p);
            remainText.Text = "−" + MediaService.Format(left) + "  ·  " + MediaService.Format(state.Duration);
        }

        private static NowPlaying With(NowPlaying s, TimeSpan at, bool playing) => new()
        {
            Title = s.Title, Artist = s.Artist, App = s.App, Cover = s.Cover, Position = at, Duration = s.Duration,
            Playing = playing, CanPrevious = s.CanPrevious, CanNext = s.CanNext, CanSeek = s.CanSeek,
        };

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            seekRequest?.Cancel();
            if (seek.IsMouseCaptureWithin) Mouse.Capture(null);
            state = null;
            coverImage.Source = null;
            backdrop.Source = null;
            backdrop.Effect = null;
            backdropLayer.BeginAnimation(OpacityProperty, null);
            backdropLayer.CacheMode = null;
        }
    }
}
