using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GraphiteCompanion.Services;

namespace GraphiteCompanion.Controls
{
    public sealed class ActivityIndicator : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
        private readonly TextBlock status = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock hint = new() { FontSize = 10, Margin = new Thickness(0, 3, 0, 0) };
        public ActivityIndicator()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
            ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
            status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubTextBrush");
            var stack = new StackPanel(); stack.Children.Add(status); stack.Children.Add(hint);
            var card = new Border { Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = stack };
            card.SetResourceReference(Border.BackgroundProperty, "PopupBrush"); card.SetResourceReference(Border.BorderBrushProperty, "AccentBorderBrush");
            Content = card;
            SourceInitialized += (_, _) => { var h = new WindowInteropHelper(this).Handle; SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80 | 0x08000000); };
        }
        public void Update(AutoClickerState state, string stopHint)
        {
            double wait = Math.Max(0, (state.StartsAt - DateTimeOffset.UtcNow).TotalSeconds);
            status.Text = wait > 0 ? $"● Старт через {Math.Ceiling(wait):0} с" : "● Автокликер" + (state.RemainingSeconds is double left ? $" · {Math.Ceiling(left):0} с" : " работает");
            hint.Text = stopHint;
            if (!IsVisible) Show();
            UpdateLayout();
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 18; Top = area.Top + 18;
        }
    }
}
