using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GraphiteCompanion.Controls;

// Overlay editing controls without contributing any size to the document layout.
internal sealed class FloatingTools : Panel
{
    private Point? position;
    public void MakeDraggable(FrameworkElement handle)
    {
        Point? origin = null; Point start = default;
        handle.MouseLeftButtonDown += (_, e) => { origin = e.GetPosition(this); start = position ?? Location(Children[0].DesiredSize, RenderSize); handle.CaptureMouse(); e.Handled = true; };
        handle.MouseMove += (_, e) => { if (origin is not Point from || e.LeftButton != MouseButtonState.Pressed) return; var at = e.GetPosition(this); position = start + (at - from); InvalidateArrange(); e.Handled = true; };
        handle.MouseLeftButtonUp += (_, e) => { origin = null; handle.ReleaseMouseCapture(); e.Handled = true; };
        handle.LostMouseCapture += (_, _) => origin = null;
    }
    private Point Location(Size child, Size available)
    {
        var desired = position ?? new Point((available.Width - child.Width) / 2, available.Height - child.Height - 8);
        return new Point(Math.Clamp(desired.X, 0, Math.Max(0, available.Width - child.Width)), Math.Clamp(desired.Y, 0, Math.Max(0, available.Height - child.Height)));
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width - 8) : 800;
        foreach (UIElement child in Children) child.Measure(new Size(width, double.PositiveInfinity));
        return new Size();
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in Children) child.Arrange(new Rect(Location(child.DesiredSize, finalSize), child.DesiredSize));
        return finalSize;
    }
}
