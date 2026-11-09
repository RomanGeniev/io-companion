using System;
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
    public sealed class NowPlayingCard : Border
    {
        private readonly MainWindow main;
        private readonly Image backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = 0, Effect = new BlurEffect { Radius = 60, RenderingBias = RenderingBias.Performance } };
        private readonly Border coverBox = new() { Width = 112, Height = 112, CornerRadius = new CornerRadius(14) };
        private readonly Image coverImage = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock coverGlyph = new() { Text = "", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 };
        private readonly TextBlock appText = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock titleText = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock artistText = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        private readonly Slider seek = new() { Margin = new Thickness(-4, 10, -4, 0) };
        private readonly TextBlock elapsedText = new() { FontSize = 11 };
        private readonly TextBlock remainText = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Grid timeRow = new();
        private readonly Button prev = new(), play = new(), next = new();
        private readonly TextBlock playGlyph;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

        private NowPlaying? state;
        private DateTime stateTime;
        private string? shownKey;
        private bool busy, dragging, settingValue;
        private DateTime ignoreUntil;     // after a seek / skip the app needs a moment before it reports the new position
        private int tick;

        private static T R<T>(string key) => (T)Application.Current.FindResource(key);

        public NowPlayingCard(MainWindow main)
        {
            this.main = main;
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

            // ---- seek bar
            seek.SetResourceReference(StyleProperty, "VolumeSlider");
            seek.Minimum = 0;
            seek.Maximum = 1;
            seek.SmallChange = 5;
            seek.LargeChange = 10;
            seek.PreviewMouseLeftButtonDown += (_, _) => { if (state?.CanSeek == true) dragging = true; };
            seek.PreviewMouseLeftButtonUp += async (_, _) =>
            {
                if (!dragging) return;
                dragging = false;
                var to = TimeSpan.FromSeconds(seek.Value);
                if (state != null) { state = With(state, to, state.Playing); stateTime = DateTime.Now; }
                ignoreUntil = DateTime.Now.AddMilliseconds(900);
                await MediaService.Seek(to);
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
            var back = new Canvas { IsHitTestVisible = false, Children = { backdrop } };
            var root = new Grid { Children = { back, body } };
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
            Loaded += async (_, _) => { timer.Start(); await Fetch(); };
            Unloaded += (_, _) => timer.Stop();
            Show();
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

        private void Skip()
        {
            ignoreUntil = DateTime.Now.AddMilliseconds(500);
            if (state != null) { state = With(state, TimeSpan.Zero, state.Playing); stateTime = DateTime.Now; Show(); }
        }

        private async System.Threading.Tasks.Task Tick()
        {
            // ask Windows twice a second, move the bar smoothly in between
            if (++tick % 2 == 0) await Fetch();
            else ShowPosition();
        }

        private async System.Threading.Tasks.Task Fetch()
        {
            if (busy) return;
            busy = true;
            try
            {
                var s = await MediaService.GetAsync();
                if (DateTime.Now < ignoreUntil && s != null && state != null && s.Key == state.Key) return;
                state = s;
                stateTime = DateTime.Now;
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
            var s = state;
            bool has = s != null && (s.Title.Length > 0 || s.Artist.Length > 0);
            appText.Text = has ? ("СЕЙЧАС ИГРАЕТ · " + s!.App.ToUpperInvariant()).TrimEnd(' ', '·') : "МУЗЫКА";
            titleText.Text = has ? (s!.Title.Length > 0 ? s.Title : "Без названия") : "Сейчас ничего не играет";
            artistText.Text = has ? s!.Artist : "Включите музыку или видео — трек, обложка и управление появятся здесь";
            artistText.Visibility = artistText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            playGlyph.Text = s?.Playing == true ? "" : "";
            prev.IsEnabled = s == null || s.CanPrevious;     // without a session the media keys are sent
            next.IsEnabled = s == null || s.CanNext;
            RefreshHints();

            string key = has ? s!.Key + (s.Cover != null ? "+c" : "") : "";
            if (key != shownKey)
            {
                shownKey = key;
                coverImage.Source = s?.Cover;
                coverGlyph.Visibility = s?.Cover == null ? Visibility.Visible : Visibility.Collapsed;
                backdrop.Source = s?.Cover;
                backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(s?.Cover != null ? 0.32 : 0, TimeSpan.FromMilliseconds(400)));
            }

            bool timeline = has && s!.Duration > TimeSpan.Zero;
            seek.Visibility = timeRow.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
            seek.IsEnabled = timeline && s!.CanSeek;
            seek.Cursor = seek.IsEnabled ? Cursors.Hand : Cursors.Arrow;
            seek.ToolTip = seek.IsEnabled ? null : "Эта программа не даёт перематывать";
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
    }
}
