using System.Windows;

namespace AnimeTierlist.Desktop.Controls;

/// <summary>
/// Pure geometry calculations for drag-and-drop insertion indices and caret placement.
/// Shared by visual drop adorners and drop handlers so the caret always matches where an item lands.
/// </summary>
public static class BoardDropCalculator
{
    /// <summary>
    /// Computes the insertion index in [0, <paramref name="itemBounds"/>.Count] for a pointer position inside
    /// a wrapping row or tile grid.
    /// </summary>
    public static int ComputeIndex(IReadOnlyList<Rect> itemBounds, Point position)
    {
        ArgumentNullException.ThrowIfNull(itemBounds);
        if (itemBounds.Count == 0)
        {
            return 0;
        }

        // First check direct hit or nearest item on the same visual row.
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < itemBounds.Count; i++)
        {
            var r = itemBounds[i];
            if (r.Contains(position))
            {
                return position.X > r.Left + (r.Width / 2) ? i + 1 : i;
            }

            // Favor items on the same horizontal band before comparing horizontal distance.
            var dy = position.Y < r.Top ? r.Top - position.Y : position.Y > r.Bottom ? position.Y - r.Bottom : 0;
            var dx = position.X < r.Left ? r.Left - position.X : position.X > r.Right ? position.X - r.Right : 0;
            var score = (dy * 1000.0) + dx;
            if (score < bestDistance)
            {
                bestDistance = score;
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return itemBounds.Count;
        }

        var nearest = itemBounds[bestIndex];
        return position.X > nearest.Left + (nearest.Width / 2) ? bestIndex + 1 : bestIndex;
    }

    /// <summary>Computes the vertical caret line segment (X, Top, Bottom) for a given insertion index.</summary>
    public static (double X, double Top, double Bottom) ComputeCaret(IReadOnlyList<Rect> itemBounds, int index, double fallbackHeight)
    {
        ArgumentNullException.ThrowIfNull(itemBounds);
        if (itemBounds.Count == 0)
        {
            return (6, 8, Math.Max(40, fallbackHeight - 8));
        }

        if (index <= 0)
        {
            var first = itemBounds[0];
            return (Math.Max(2, first.Left - 3), first.Top + 4, first.Bottom - 4);
        }

        if (index >= itemBounds.Count)
        {
            var last = itemBounds[^1];
            return (last.Right + 3, last.Top + 4, last.Bottom - 4);
        }

        var prev = itemBounds[index - 1];
        var next = itemBounds[index];
        // If prev and next are on the same wrapped line, place the caret midway between them; otherwise at next's left edge.
        if (Math.Abs(prev.Top - next.Top) < prev.Height * 0.5)
        {
            var x = (prev.Right + next.Left) / 2.0;
            return (x, next.Top + 4, next.Bottom - 4);
        }

        return (Math.Max(2, next.Left - 3), next.Top + 4, next.Bottom - 4);
    }

    /// <summary>One realized item's actual list index and visual bounds inside its parent list.</summary>
    public readonly record struct IndexedRect(int Index, Rect Bounds);

    /// <summary>
    /// Computes the insertion index in [0, <paramref name="totalCount"/>] and horizontal caret geometry (Y, Left, Right)
    /// for a vertical list whose items may be virtualized and scrolled.
    /// </summary>
    public static (int InsertIndex, double Y, double Left, double Right) ComputeVerticalDrop(
        IReadOnlyList<IndexedRect> realizedItems, int totalCount, Point position, double listWidth)
    {
        ArgumentNullException.ThrowIfNull(realizedItems);
        var right = Math.Max(24, listWidth - 8);
        if (realizedItems.Count == 0)
        {
            return (0, 4, 8, right);
        }

        for (var i = 0; i < realizedItems.Count; i++)
        {
            var current = realizedItems[i];
            if (position.Y < current.Bounds.Top + (current.Bounds.Height / 2.0))
            {
                var y = i > 0 && realizedItems[i - 1].Index == current.Index - 1
                    ? (realizedItems[i - 1].Bounds.Bottom + current.Bounds.Top) / 2.0
                    : Math.Max(2, current.Bounds.Top);
                return (current.Index, y, 8, right);
            }
        }

        var last = realizedItems[^1];
        return (Math.Min(totalCount, last.Index + 1), last.Bounds.Bottom, 8, right);
    }
}
