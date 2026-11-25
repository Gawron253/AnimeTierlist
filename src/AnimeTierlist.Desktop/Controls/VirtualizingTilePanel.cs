using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AnimeTierlist.Desktop.Controls;

/// <summary>
/// A pure-WPF virtualizing grid panel for poster cards. Realizes only the visible rows plus one overscan row above
/// and below, recycles containers via <see cref="IRecyclingItemContainerGenerator"/>, and supports
/// <see cref="BringIndexIntoView"/> so keyboard navigation reaches unrealized items cleanly.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty TileWidthProperty = DependencyProperty.Register(
        nameof(TileWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(148.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty TileHeightProperty = DependencyProperty.Register(
        nameof(TileHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(236.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty HorizontalGapProperty = DependencyProperty.Register(
        nameof(HorizontalGap), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty VerticalGapProperty = DependencyProperty.Register(
        nameof(VerticalGap), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CenterContentProperty = DependencyProperty.Register(
        nameof(CenterContent), typeof(bool), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _columns = 1;

    /// <summary>Width of each realized poster card in DIPs.</summary>
    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    /// <summary>Height of each realized poster card in DIPs.</summary>
    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    /// <summary>Horizontal spacing between adjacent cards in DIPs.</summary>
    public double HorizontalGap
    {
        get => (double)GetValue(HorizontalGapProperty);
        set => SetValue(HorizontalGapProperty, value);
    }

    /// <summary>Vertical spacing between adjacent rows in DIPs.</summary>
    public double VerticalGap
    {
        get => (double)GetValue(VerticalGapProperty);
        set => SetValue(VerticalGapProperty, value);
    }

    /// <summary>When true, horizontally centers the tile columns inside the viewport; when false, left-aligns.</summary>
    public bool CenterContent
    {
        get => (bool)GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    /// <summary>Computed column count for the current viewport width.</summary>
    public int Columns => _columns;

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - Math.Max(32, RowStride / 3));

    public void LineDown() => SetVerticalOffset(VerticalOffset + Math.Max(32, RowStride / 3));

    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 24);

    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 24);

    public void PageUp() => SetVerticalOffset(VerticalOffset - Math.Max(RowStride, _viewport.Height - RowStride / 2));

    public void PageDown() => SetVerticalOffset(VerticalOffset + Math.Max(RowStride, _viewport.Height - RowStride / 2));

    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(HorizontalOffset + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - Math.Max(48, RowStride / 2));

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + Math.Max(48, RowStride / 2));

    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - 48);

    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + 48);

    public void SetHorizontalOffset(double offset)
    {
        var clamped = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        if (Math.Abs(clamped - _offset.X) < 0.01)
        {
            return;
        }

        _offset.X = clamped;
        InvalidateArrange();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(clamped - _offset.Y) < 0.01)
        {
            return;
        }

        _offset.Y = clamped;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is UIElement element && InternalChildren.Contains(element))
        {
            var itemsControl = ItemsControl.GetItemsOwner(this);
            if (itemsControl is not null)
            {
                var index = itemsControl.ItemContainerGenerator.IndexFromContainer(element);
                if (index >= 0)
                {
                    ScrollIndexIntoView(index);
                }
            }
        }

        return rectangle;
    }

    /// <summary>Computes the pure layout geometry for an item index given the current tile metrics and column count.</summary>
    public Rect GetTileRect(int index)
    {
        var col = index % _columns;
        var row = index / _columns;
        return new Rect(col * ColumnStride, row * RowStride, TileWidth, TileHeight);
    }

    protected override void BringIndexIntoView(int index)
    {
        ScrollIndexIntoView(index);
        UpdateLayout();
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                if (args.ItemUICount > 0
                    && args.Position.Index >= 0
                    && args.Position.Index + args.ItemUICount <= InternalChildren.Count)
                {
                    RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                }

                break;

            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0
                    && args.OldPosition.Index >= 0
                    && args.OldPosition.Index + args.ItemUICount <= InternalChildren.Count)
                {
                    if (args.Position.Offset == 0 && args.Position.Index >= 0)
                    {
                        var moved = new UIElement[args.ItemUICount];
                        for (var i = 0; i < args.ItemUICount; i++)
                        {
                            moved[i] = InternalChildren[args.OldPosition.Index + i];
                        }

                        RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                        var insertAt = Math.Clamp(args.Position.Index, 0, InternalChildren.Count);
                        for (var i = 0; i < moved.Length; i++)
                        {
                            InsertInternalChild(insertAt + i, moved[i]);
                        }
                    }
                    else
                    {
                        // The generator moved the item into an unrealized gap and de-realized its container.
                        RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                    }
                }

                break;

            case NotifyCollectionChangedAction.Reset:
                // Preserve scroll offset across filter refreshes; UpdateScrollInfo clamps it to the new extent.
                ScrollOwner?.InvalidateScrollInfo();
                break;
        }

        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsControl?.Items.Count ?? 0;
        var viewportWidth = double.IsInfinity(availableSize.Width) ? Math.Max(TileWidth, 640) : Math.Max(TileWidth, availableSize.Width);
        var viewportHeight = double.IsInfinity(availableSize.Height) ? Math.Max(TileHeight, 480) : availableSize.Height;

        _columns = Math.Max(1, (int)Math.Floor((viewportWidth + HorizontalGap) / ColumnStride));
        var rows = itemCount == 0 ? 0 : (itemCount + _columns - 1) / _columns;
        var extentHeight = rows == 0 ? 0 : (rows * RowStride) - VerticalGap;
        UpdateScrollInfo(new Size(viewportWidth, viewportHeight), new Size(viewportWidth, extentHeight));

        if (itemCount == 0 || ItemContainerGenerator is null)
        {
            RecycleRange(0, InternalChildren.Count);
            return new Size(viewportWidth, Math.Min(viewportHeight, extentHeight));
        }

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(_offset.Y / RowStride) - 1);
        var lastVisibleRow = Math.Min(rows - 1, (int)Math.Floor((_offset.Y + viewportHeight) / RowStride) + 1);
        var firstIndex = Math.Clamp(firstVisibleRow * _columns, 0, itemCount - 1);
        var lastIndex = Math.Clamp(((lastVisibleRow + 1) * _columns) - 1, firstIndex, itemCount - 1);

        // Accessing InternalChildren initializes the WPF ItemContainerGenerator before use.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        var tileSize = new Size(TileWidth, TileHeight);
        using (generator.StartAt(startPos, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                if (generator.GenerateNext(out var newlyRealized) is not UIElement child)
                {
                    break;
                }

                if (newlyRealized)
                {
                    if (childIndex >= children.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }
                else
                {
                    var currentIndex = children.IndexOf(child);
                    if (currentIndex < 0)
                    {
                        // A recycled container comes back with newlyRealized == false. The generator has already
                        // re-linked its DataContext, but its Content still holds the cleared previous item until
                        // it is prepared again; skipping this showed the wrong cover and title on the card.
                        if (childIndex >= children.Count)
                        {
                            AddInternalChild(child);
                        }
                        else
                        {
                            InsertInternalChild(childIndex, child);
                        }

                        generator.PrepareItemContainer(child);
                    }
                    else if (currentIndex != childIndex)
                    {
                        RemoveInternalChildRange(currentIndex, 1);
                        var targetIndex = Math.Clamp(childIndex, 0, children.Count);
                        InsertInternalChild(targetIndex, child);
                    }
                }

                child.Measure(tileSize);
            }
        }

        CleanUpOutsideRange(firstIndex, lastIndex);

        var desiredWidth = double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width;
        var desiredHeight = double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height;
        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return finalSize;
        }

        var children = InternalChildren;
        var gridWidth = (_columns * TileWidth) + (Math.Max(0, _columns - 1) * HorizontalGap);
        var centerOffset = CenterContent ? Math.Floor(Math.Max(0, finalSize.Width - gridWidth) / 2) : 0;
        for (var i = 0; i < children.Count; i++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0)
            {
                continue;
            }

            var rect = GetTileRect(itemIndex);
            rect.Offset(centerOffset - _offset.X, -_offset.Y);
            children[i].Arrange(rect);
        }

        return finalSize;
    }

    private double ColumnStride => Math.Max(16, TileWidth + HorizontalGap);

    private double RowStride => Math.Max(16, TileHeight + VerticalGap);

    private void ScrollIndexIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

        var row = index / _columns;
        var itemTop = row * RowStride;
        var itemBottom = itemTop + TileHeight;
        if (itemTop < _offset.Y)
        {
            SetVerticalOffset(itemTop);
        }
        else if (itemBottom > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(Math.Max(0, itemBottom - _viewport.Height));
        }
    }

    private void CleanUpOutsideRange(int minDesiredIndex, int maxDesiredIndex)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var recycler = generator as IRecyclingItemContainerGenerator;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(pos);
            if (itemIndex < 0)
            {
                RemoveInternalChildRange(i, 1);
                continue;
            }

            if (itemIndex < minDesiredIndex || itemIndex > maxDesiredIndex)
            {
                if (children[i] is UIElement element && element.IsKeyboardFocusWithin)
                {
                    continue;
                }

                if (recycler is not null)
                {
                    recycler.Recycle(pos, 1);
                }
                else
                {
                    generator.Remove(pos, 1);
                }

                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private void RecycleRange(int startIndex, int count)
    {
        if (count <= 0 || ItemContainerGenerator is null)
        {
            return;
        }

        var pos = new GeneratorPosition(startIndex, 0);
        if (ItemContainerGenerator is IRecyclingItemContainerGenerator recycler)
        {
            recycler.Recycle(pos, count);
        }
        else
        {
            ItemContainerGenerator.Remove(pos, count);
        }

        RemoveInternalChildRange(startIndex, count);
    }

    private void UpdateScrollInfo(Size viewport, Size extent)
    {
        var maxOffsetY = Math.Max(0, extent.Height - viewport.Height);
        if (_offset.Y > maxOffsetY)
        {
            _offset.Y = maxOffsetY;
        }

        if (_viewport != viewport || _extent != extent)
        {
            _viewport = viewport;
            _extent = extent;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }
}
