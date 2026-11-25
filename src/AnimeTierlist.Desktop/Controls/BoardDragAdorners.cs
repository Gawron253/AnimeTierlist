using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AnimeTierlist.Desktop.Controls;

/// <summary>Translucent ghost preview of a dragged show card rendered on the window adorner layer.</summary>
public sealed class DragPreviewAdorner : Adorner
{
    private readonly VisualBrush _brush;
    private readonly Size _size;
    private Point _position;

    /// <summary>Creates a ghost preview from the dragged visual element.</summary>
    public DragPreviewAdorner(UIElement adornedElement, UIElement draggedVisual, Point startPosition)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
        _size = new Size(Math.Max(64, draggedVisual.RenderSize.Width), Math.Max(88, draggedVisual.RenderSize.Height));
        _brush = new VisualBrush(draggedVisual) { Opacity = 0.82, Stretch = Stretch.Uniform };
        _position = startPosition;
    }

    /// <summary>Updates the cursor-anchored preview position.</summary>
    public void UpdatePosition(Point position)
    {
        if (Math.Abs(position.X - _position.X) < 0.5 && Math.Abs(position.Y - _position.Y) < 0.5)
        {
            return;
        }

        _position = position;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(_position.X - (_size.Width / 2), _position.Y - (_size.Height / 2), _size.Width, _size.Height);
        var borderBrush = TryFindResource("Brush.AccentFill") as Brush ?? Brushes.DodgerBlue;
        var pen = new Pen(borderBrush, 2);
        drawingContext.DrawRoundedRectangle(_brush, pen, rect, 6, 6);
    }
}

/// <summary>
/// Renders a crisp 3-DIP vertical insertion caret with end-caps inside a target tier row or collection list
/// so the user sees the exact drop position before releasing the mouse.
/// </summary>
public sealed class DropInsertionAdorner : Adorner
{
    private double _x;
    private double _top;
    private double _bottom;
    private bool _showCaret;

    /// <summary>Creates a drop insertion adorner attached to the hovered row container.</summary>
    public DropInsertionAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    /// <summary>Sets the caret line coordinates and visibility.</summary>
    public void UpdateCaret(double x, double top, double bottom, bool showCaret)
    {
        _x = x;
        _top = top;
        _bottom = bottom;
        _showCaret = showCaret;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var accent = TryFindResource("Brush.AccentFill") as Brush ?? Brushes.DodgerBlue;
        var bounds = new Rect(new Point(0, 0), AdornedElement.RenderSize);
        var outlinePen = new Pen(accent, 2);
        drawingContext.DrawRoundedRectangle(null, outlinePen, bounds, 6, 6);

        if (!_showCaret)
        {
            return;
        }

        var caretPen = new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawLine(caretPen, new Point(_x, _top), new Point(_x, _bottom));
        drawingContext.DrawEllipse(accent, null, new Point(_x, _top), 4, 4);
        drawingContext.DrawEllipse(accent, null, new Point(_x, _bottom), 4, 4);
    }
}

/// <summary>
/// Renders a crisp 3-DIP horizontal insertion line with end-caps inside a vertical sidebar list
/// while dragging to reorder collections or boards.
/// </summary>
public sealed class HorizontalDropInsertionAdorner : Adorner
{
    private double _y;
    private double _left;
    private double _right;

    /// <summary>Creates a horizontal drop insertion adorner attached to the target list.</summary>
    public HorizontalDropInsertionAdorner(UIElement adornedElement)
        : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    /// <summary>Sets the horizontal insertion line coordinates.</summary>
    public void UpdateLine(double y, double left, double right)
    {
        _y = y;
        _left = left;
        _right = right;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var accent = TryFindResource("Brush.AccentFill") as Brush ?? Brushes.DodgerBlue;
        var pen = new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        drawingContext.DrawLine(pen, new Point(_left, _y), new Point(_right, _y));
        drawingContext.DrawEllipse(accent, null, new Point(_left, _y), 3.5, 3.5);
        drawingContext.DrawEllipse(accent, null, new Point(_right, _y), 3.5, 3.5);
    }
}
