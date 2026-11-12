using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;

namespace GraphiteCompanion.Controls;

// A drag belongs to one editor/group; unrelated nested groups never consume it.
internal static class BlockDrag
{
    private sealed record Payload(object Scope, string Id);
    public static Border Handle(string title, object scope, string id, Action hide)
    {
        var row = new DockPanel();
        var close = new Button { Content = "−", ToolTip = "Скрыть блок (можно вернуть в списке)", Padding = new Thickness(7, 0, 7, 0) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        close.Click += (_, e) => { e.Handled = true; hide(); }; DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
        var label = new TextBlock { Text = "⠿  " + title, Padding = new Thickness(7), TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.SizeAll };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); row.Children.Add(label);
        var handle = new Border { Child = row, Margin = new Thickness(0, 3, 0, 3), CornerRadius = new CornerRadius(5) };
        handle.SetResourceReference(Border.BackgroundProperty, "SelectedBrush");
        Point start = default; bool pressed = false;
        label.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(label); pressed = true; label.CaptureMouse(); e.Handled = true; };
        label.MouseLeftButtonUp += (_, e) => { pressed = false; label.ReleaseMouseCapture(); e.Handled = true; };
        label.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(label);
            if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressed = false; label.ReleaseMouseCapture();
            DragDrop.DoDragDrop(label, new DataObject(typeof(Payload), new Payload(scope, id)), DragDropEffects.Move);
        };
        return handle;
    }
    public static void Target(Border target, object scope, bool horizontal, Action<string, bool> drop)
    {
        target.AllowDrop = true;
        Payload? Read(DragEventArgs e) => e.Data.GetData(typeof(Payload)) is Payload value && ReferenceEquals(value.Scope, scope) ? value : null;
        void Clear() { target.BorderThickness = new Thickness(0); }
        target.DragOver += (_, e) =>
        {
            if (Read(e) == null) return;
            var point = e.GetPosition(target); bool after = horizontal ? point.X > target.ActualWidth / 2 : point.Y > target.ActualHeight / 2;
            target.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            target.BorderThickness = horizontal ? new Thickness(after ? 0 : 3, 0, after ? 3 : 0, 0) : new Thickness(0, after ? 0 : 3, 0, after ? 3 : 0);
            e.Effects = DragDropEffects.Move; e.Handled = true;
        };
        target.DragLeave += (_, _) => Clear();
        target.Drop += (_, e) =>
        {
            if (Read(e) is not { } value) return;
            var point = e.GetPosition(target); bool after = horizontal ? point.X > target.ActualWidth / 2 : point.Y > target.ActualHeight / 2;
            Clear(); e.Handled = true; drop(value.Id, after);
        };
    }
    public static void PlaceBeside(Window editor, Window owner)
    {
        if (owner.Left < -10000) return;
        var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(owner).Handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(owner);
        double left = screen.Left / dpi.DpiScaleX, right = screen.Right / dpi.DpiScaleX, top = screen.Top / dpi.DpiScaleY, bottom = screen.Bottom / dpi.DpiScaleY;
        editor.MaxHeight = Math.Max(260, bottom - top - 24);
        double x = owner.Left + owner.ActualWidth + 10;
        if (x + editor.ActualWidth > right) x = owner.Left - editor.ActualWidth - 10;
        if (x < left) x = right - editor.ActualWidth - 12;
        editor.Left = Math.Clamp(x, left, Math.Max(left, right - editor.ActualWidth));
        editor.Top = Math.Clamp(owner.Top, top + 12, Math.Max(top + 12, bottom - editor.ActualHeight - 12));
    }
}
