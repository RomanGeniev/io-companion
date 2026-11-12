using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace GraphiteCompanion.Controls;

// A single snapshot follows the grab point; the real control previews its future layout.
internal sealed class DragPreview : Adorner, IDisposable
{
    private static DragPreview? current;
    public static bool Active => current != null;
    private readonly AdornerLayer layer;
    private readonly Point grab;
    private readonly ImageSource image;
    private readonly Size size;
    private Point position;
    private readonly Window? owner;
    private readonly SnapBoard? board;
    public event Action? Ended;
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    internal static Point PointerOn(Visual target,Point fallback)
        => GetCursorPos(out var point)?target.PointFromScreen(new Point(point.X,point.Y)):fallback;

    private DragPreview(EditTile tile, FrameworkElement root, AdornerLayer layer, Point grab) : base(root)
    {
        this.layer = layer;
        var transform=tile.TransformToAncestor(root);
        var bounds=transform.TransformBounds(new Rect(tile.RenderSize));
        var grabbedPoint=transform.Transform(grab);this.grab=new Point(grabbedPoint.X-bounds.X,grabbedPoint.Y-bounds.Y);
        for (DependencyObject? at = tile; at != null; at = VisualTreeHelper.GetParent(at)) if (at is SnapBoard found) { board = found; break; }
        board?.ShowDragTools(true);
        IsHitTestVisible = false; image = tile.Snapshot(); size = bounds.Size;
        owner = Window.GetWindow(tile);
        if (owner != null) owner.AddHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(Feedback), true);
        layer.Add(this); Update();
    }
    public static DragPreview Begin(EditTile tile, Point grab)
    {
        DependencyObject? node = tile;
        while (node != null && node is not SnapBoard) node = VisualTreeHelper.GetParent(node);
        var root = (node as SnapBoard)?.DragSurface ?? tile;
        var layer = AdornerLayer.GetAdornerLayer(root) ?? AdornerLayer.GetAdornerLayer(tile)
            ?? throw new InvalidOperationException("Layout editor requires an adorner layer.");
        return current = new DragPreview(tile, root, layer, grab);
    }
    private void Feedback(object sender, GiveFeedbackEventArgs e) { Update(); e.UseDefaultCursors = false; Mouse.SetCursor(Cursors.SizeAll); e.Handled = true; }
    private void Update()
    {
        // Keep the grabbed image and dimensions fixed until release. The underlying
        // layout previews the destination; it must never stretch the image in the hand.
        if (GetCursorPos(out var point)) {
            var at = AdornedElement.PointFromScreen(new Point(point.X, point.Y));
            position = new Point(at.X - Math.Min(grab.X, size.Width - 4), at.Y - Math.Min(grab.Y, size.Height - 4));
        }
        InvalidateVisual();
    }
    public static void Refresh() { if (current == null) return; current.AdornedElement.UpdateLayout(); current.Update(); }
    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(position, size);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null, new Rect(position + new Vector(5, 7), size), 9, 9);
        dc.DrawRoundedRectangle(TryFindResource("CardBrush") as Brush ?? Brushes.DimGray, null, rect, 8, 8); dc.DrawImage(image, rect);
        var brush = TryFindResource("CardBorderBrush") as Brush ?? Brushes.Gray;
        dc.DrawRoundedRectangle(null, new Pen(brush, 1.5), rect, 8, 8);
    }
    public void Dispose()
    {
        if (owner != null) owner.RemoveHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler(Feedback));
        layer.Remove(this); current = null; board?.ShowDragTools(false); Mouse.SetCursor(null); Ended?.Invoke();
    }
}
