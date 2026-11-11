using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GraphiteCompanion.Services;
using Drawing = System.Drawing;

namespace GraphiteCompanion.Controls
{
    /// <summary>Physical desktop coordinates, with an independently scaled surface on every display.</summary>
    public sealed class RegionSelection
    {
        private readonly List<SelectionWindow> windows = new();
        private readonly TaskCompletionSource<Drawing.Rectangle?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Drawing.Point? anchor;
        private Drawing.Rectangle selection;
        private bool finished;
        public static Drawing.Rectangle RectangleBetween(Drawing.Point a, Drawing.Point b) => Drawing.Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        public Task<Drawing.Rectangle?> Show(IEnumerable<Drawing.Rectangle> screens)
        {
            try
            {
                foreach (var bounds in screens) windows.Add(new SelectionWindow(this, bounds));
                if (windows.Count == 0) { Cancel(); return completion.Task; }
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
                foreach (var window in windows) window.Show();
                windows[0].Activate(); windows[0].Focus();
            }
            catch { Cancel(); throw; }
            return completion.Task;
        }
        private void DisplayChanged(object? sender, EventArgs args) => Application.Current.Dispatcher.BeginInvoke(new Action(Cancel));
        public void Cancel() => Finish(null);
        private void Finish(Drawing.Rectangle? result)
        {
            if (finished) return;
            finished = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            Mouse.Capture(null);
            foreach (var window in windows) window.Close();
            completion.TrySetResult(result);
        }
        private void Move()
        {
            if (anchor is not { } start || WindowsAutoClickInput.Cursor() is not { } point) return;
            selection = RectangleBetween(start, new Drawing.Point(point.X, point.Y));
            foreach (var window in windows) window.InvalidateVisual();
        }
        private sealed class SelectionWindow : Window
        {
            [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
            [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
            private readonly RegionSelection owner;
            private readonly Drawing.Rectangle bounds;
            public SelectionWindow(RegionSelection owner, Drawing.Rectangle bounds)
            {
                this.owner = owner; this.bounds = bounds;
                WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
                AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); Cursor = Cursors.Cross;
                Width = bounds.Width; Height = bounds.Height;
                SourceInitialized += (_, _) =>
                {
                    var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
                    try { SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x40); }
                    finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
                };
                PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; owner.Cancel(); } };
                MouseRightButtonDown += (_, _) => owner.Cancel();
                MouseLeftButtonDown += (_, e) =>
                {
                    if (WindowsAutoClickInput.Cursor() is not { } point) return;
                    owner.anchor = new Drawing.Point(point.X, point.Y); CaptureMouse(); owner.Move(); e.Handled = true;
                };
                MouseMove += (_, _) => owner.Move();
                MouseLeftButtonUp += (_, e) =>
                {
                    if (owner.anchor == null) return;
                    owner.Move(); var result = owner.selection;
                    owner.Finish(result.Width >= 2 && result.Height >= 2 ? result : null); e.Handled = true;
                };
                LostMouseCapture += (_, _) => { if (!owner.finished && owner.anchor != null) owner.Cancel(); };
                Closed += (_, _) => owner.Cancel();
            }
            protected override void OnRender(DrawingContext dc)
            {
                base.OnRender(dc);
                if (ActualWidth <= 0 || ActualHeight <= 0) return;
                var full = new Rect(0, 0, ActualWidth, ActualHeight);
                var cut = Drawing.Rectangle.Intersect(bounds, owner.selection);
                Rect hole = cut.Width > 0 && cut.Height > 0 ? new Rect((cut.X - bounds.X) * ActualWidth / bounds.Width, (cut.Y - bounds.Y) * ActualHeight / bounds.Height, cut.Width * ActualWidth / bounds.Width, cut.Height * ActualHeight / bounds.Height) : Rect.Empty;
                Geometry shade = new RectangleGeometry(full);
                if (!hole.IsEmpty) shade = new CombinedGeometry(GeometryCombineMode.Exclude, shade, new RectangleGeometry(hole));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)), null, shade);
                if (!hole.IsEmpty) dc.DrawRectangle(null, new Pen((Brush)FindResource("AccentBrush"), 2), hole);
                string label = owner.anchor == null ? "Выделите область · Esc — отмена" : $"{owner.selection.Width} × {owner.selection.Height} · отпустите мышь";
                var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, (Brush)FindResource("TextBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var box = new Rect((ActualWidth - text.Width - 32) / 2, 20, text.Width + 32, 42);
                dc.DrawRoundedRectangle((Brush)FindResource("PopupBrush"), new Pen((Brush)FindResource("AccentBorderBrush"), 1), box, 10, 10);
                dc.DrawText(text, new Point(box.X + 16, box.Y + 11));
            }
        }
    }
}
