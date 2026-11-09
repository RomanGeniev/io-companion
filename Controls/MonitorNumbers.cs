using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GraphiteCompanion.Controls
{
    /// <summary>
    /// Big monitor numbers, one per screen, like "Identify" in Windows display settings.
    /// Click-through and never takes focus. Centre of the screen, or its top-left corner (while the picker is open).
    /// </summary>
    public sealed class MonitorNumbers : IDisposable
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private readonly List<Window> windows = new();

        /// <summary>Shows the numbers; they close after <paramref name="duration"/> or when disposed.</summary>
        public static MonitorNumbers Show(IList<(System.Drawing.Rectangle bounds, int number, string details)> screens, TimeSpan? duration, bool centre)
        {
            var n = new MonitorNumbers();
            foreach (var (bounds, number, details) in screens) n.windows.Add(Make(bounds, number, details, centre));
            if (duration is { } d)
            {
                var t = new DispatcherTimer { Interval = d };
                t.Tick += (_, _) => { t.Stop(); n.Dispose(); };
                t.Start();
            }
            return n;
        }

        private static Window Make(System.Drawing.Rectangle bounds, int number, string details, bool centre)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = number.ToString(), FontSize = centre ? 140 : 72, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White, Margin = new Thickness(0, -10, 0, -6),
            });
            var label = new TextBlock { Text = "Монитор " + number, FontSize = centre ? 20 : 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
            stack.Children.Add(label);
            if (details.Length > 0)
                stack.Children.Add(new TextBlock { Text = details, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 0, 0) });

            var card = new Border
            {
                Padding = centre ? new Thickness(48, 18, 48, 22) : new Thickness(28, 10, 28, 14),
                CornerRadius = new CornerRadius(centre ? 28 : 20), Margin = new Thickness(20),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Opacity = 0.6 },
                Child = stack,
            };
            card.SetResourceReference(Border.BackgroundProperty, "AccentGradient");

            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
                ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.WidthAndHeight,
                FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), Content = card,
                Left = -32000, Top = -32000,
            };
            w.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80 | 0x08000000); // transparent, tool window, no activate
            };
            w.ContentRendered += (_, _) =>
            {
                var src = PresentationSource.FromVisual(w);
                if (src?.CompositionTarget == null) return;
                var m = src.CompositionTarget.TransformToDevice;
                int pw = (int)(w.ActualWidth * m.M11), ph = (int)(w.ActualHeight * m.M22);
                int x = centre ? bounds.Left + (bounds.Width - pw) / 2 : bounds.Left + 24;
                int y = centre ? bounds.Top + (bounds.Height - ph) / 2 : bounds.Top + 24;
                SetWindowPos(new WindowInteropHelper(w).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); // TOPMOST, NOSIZE|NOACTIVATE
            };
            w.Show();
            return w;
        }

        public void Dispose()
        {
            foreach (var w in windows) { try { w.Close(); } catch { } }
            windows.Clear();
        }
    }
}
