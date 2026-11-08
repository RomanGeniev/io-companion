using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GraphiteCompanion.Controls
{
    /// <summary>
    /// Small on-screen hint ("🎮 Игры · Ткань") that never steals focus and lets clicks pass through.
    /// Shown above windowed / borderless games (not exclusive fullscreen).
    /// </summary>
    public sealed class OsdWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTopmost = 0x8;

        private readonly TextBlock icon;
        private readonly TextBlock title;
        private readonly TextBlock subtitle;
        private readonly DispatcherTimer hideTimer;

        public OsdWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            Opacity = 0;

            icon = new TextBlock
            {
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 26, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            };
            title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            subtitle = new TextBlock { FontSize = 12 };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");

            var iconBox = new Border
            {
                Width = 48, Height = 48, CornerRadius = new CornerRadius(14),

                Child = new Grid { Children = { icon } },
            };
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            iconBox.SetResourceReference(Border.BackgroundProperty, "AccentGradient");

            var texts = new StackPanel { Margin = new Thickness(14, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(title);
            texts.Children.Add(subtitle);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(iconBox);
            row.Children.Add(texts);

            var card = new Border
            {
                Margin = new Thickness(20),
                Padding = new Thickness(14, 12, 22, 12),
                CornerRadius = new CornerRadius(18),


                BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.6 },
                Child = row,
            };
            card.SetResourceReference(Border.BackgroundProperty, "PopupBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "AccentBorderBrush");
            Content = card;

            hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            hideTimer.Tick += (_, _) => { hideTimer.Stop(); Fade(0); };

            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, GwlExStyle, GetWindowLong(h, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExTopmost);
            };
        }

        public void ShowMessage(string glyph, string text, string? sub = null)
        {
            icon.Text = glyph;
            title.Text = text;
            subtitle.Text = sub ?? "";
            subtitle.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;

            if (!IsVisible) Show();
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + area.Height * 0.78 - ActualHeight / 2;
            Topmost = false;
            Topmost = true;

            Fade(1);
            hideTimer.Stop();
            hideTimer.Start();
        }

        private void Fade(double to)
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 140 : 320));
            if (to == 0) anim.Completed += (_, _) => { if (Opacity == 0) Hide(); };
            BeginAnimation(OpacityProperty, anim);
        }
    }
}
