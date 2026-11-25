using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AnimeTierlist.Desktop.Controls;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>
/// View mechanics for a board: drag-and-drop with live ghost preview and insertion caret adorners,
/// edge scrolling, keeping focus on a moved show, proportional unranked tray sizing, and responsive inspector overlay.
/// </summary>
public partial class BoardView : UserControl
{
    private const double TrayHeaderHeight = 32;
    private Point _dragStart;
    private BoardEntryViewModel? _dragEntry;
    private ListBoxItem? _dragVisual;
    private BoardViewModel? _board;
    private DragPreviewAdorner? _ghostAdorner;
    private DropInsertionAdorner? _caretAdorner;
    private ListBox? _caretTargetList;
    private TierViewModel? _highlightedTier;
    private IInputElement? _focusBeforeOverlay;
    private bool _isCompactWidth;
    private bool _overlayActive;
    private bool _syncingTrayHeight;

    public BoardView()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCompactWidth = ActualWidth > 0 && ActualWidth < 960;
        if (_isCompactWidth && _board is { InspectorOpen: true })
        {
            _board.InspectorOpen = false;
        }

        ApplyInspectorPlacement();
        SyncTrayRowHeight();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_board is not null)
        {
            _board.SelectionMoved -= OnSelectionMoved;
            _board.PropertyChanged -= OnBoardPropertyChanged;
        }

        _board = DataContext as BoardViewModel;
        if (_board is not null)
        {
            _board.SelectionMoved += OnSelectionMoved;
            _board.PropertyChanged += OnBoardPropertyChanged;
        }

        if (_isCompactWidth && _board is { InspectorOpen: true })
        {
            _board.InspectorOpen = false;
        }

        ApplyInspectorPlacement();
        SyncTrayRowHeight();
    }

    private void OnBoardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardViewModel.InspectorOpen))
        {
            ApplyInspectorPlacement();
        }
        else if (e.PropertyName is nameof(BoardViewModel.TrayCollapsed) or nameof(BoardViewModel.TrayHeight) or nameof(BoardViewModel.Density))
        {
            SyncTrayRowHeight();
        }
        else if (e.PropertyName == nameof(BoardViewModel.IsRenamingInline) && _board?.IsRenamingInline == true)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                InlineRenameBox.Focus();
                InlineRenameBox.SelectAll();
            });
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 960;
        if (compact != _isCompactWidth)
        {
            _isCompactWidth = compact;
            if (compact && _board is { InspectorOpen: true })
            {
                _board.InspectorOpen = false;
            }

            ApplyInspectorPlacement();
        }

        SyncTrayRowHeight();
    }

    private void OnBoardRowsGridSizeChanged(object sender, SizeChangedEventArgs e) => SyncTrayRowHeight();

    private void OnTraySplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_board is { TrayCollapsed: false, IsCompactViewport: false } && TrayRow.ActualHeight >= 74 + TrayHeaderHeight)
        {
            _board.TrayHeight = Math.Clamp(TrayRow.ActualHeight - TrayHeaderHeight, 74, 420);
        }
    }

    private void SyncTrayRowHeight()
    {
        if (_board is null || _syncingTrayHeight)
        {
            return;
        }

        _syncingTrayHeight = true;
        try
        {
            var available = BoardRowsGrid.ActualHeight;
            // On compact viewports (960x540 DIP, with or without Save-Failed banner), adapt card height without changing saved Density.
            _board.CompactViewportLevel = available > 0 && available < 310
                ? 2
                : available > 0 && available < 390
                    ? 1
                    : 0;

            if (_board.TrayCollapsed)
            {
                TrayRow.Height = GridLength.Auto;
                TrayRow.MinHeight = 0;
                TrayRow.MaxHeight = double.PositiveInfinity;
                return;
            }

            var desiredTotal = _board.TrayHeight + TrayHeaderHeight;
            if (available > 0)
            {
                // Exact 1-row tray height: tile + tray header + ListBox padding/borders (tightened on compact viewports so two full tiers fit above).
                var oneRowTray = _board.TileHeight + TrayHeaderHeight + (_board.CompactViewportLevel >= 1 ? 12 : 14);
                if (_board.CompactViewportLevel >= 1)
                {
                    // On compact viewports (960x540 DIP), size the tray snugly to one card row with zero scrollbar so two full tiers stay visible above.
                    var maxCompactTray = Math.Max(104, available - _board.RowMinHeight - 7);
                    var compactTray = Math.Min(oneRowTray, maxCompactTray);
                    TrayRow.MinHeight = Math.Min(104, compactTray);
                    TrayRow.MaxHeight = maxCompactTray;
                    TrayRow.Height = new GridLength(compactTray, GridUnitType.Pixel);
                    return;
                }

                // Reserve at least one full tier row above the splitter while keeping at least one complete card + title in the tray.
                var minTierViewport = Math.Min(available * 0.48, _board.TileHeight + 20);
                var maxTray = Math.Max(110, available - minTierViewport - 7);
                var minTrayForOneCard = Math.Min(maxTray, Math.Max(110, oneRowTray));
                var clamped = Math.Clamp(desiredTotal, minTrayForOneCard, maxTray);
                TrayRow.MinHeight = Math.Min(110, maxTray);
                TrayRow.MaxHeight = maxTray;
                TrayRow.Height = new GridLength(clamped, GridUnitType.Pixel);
            }
            else
            {
                TrayRow.MinHeight = 110;
                TrayRow.MaxHeight = 456;
                TrayRow.Height = new GridLength(Math.Clamp(desiredTotal, 110, 456), GridUnitType.Pixel);
            }
        }
        finally
        {
            _syncingTrayHeight = false;
        }
    }

    /// <summary>
    /// Synchronizes docked vs overlay inspector placement and centralizes focus capture/restoration
    /// across load, resize, and open/close transitions so focus never stays behind the scrim.
    /// </summary>
    private void ApplyInspectorPlacement(bool restoreFocus = true)
    {
        var nextOverlayActive = _isCompactWidth && _board?.InspectorOpen == true;
        if (_isCompactWidth)
        {
            BoardInspectorColumn.Width = new GridLength(0);
            Grid.SetColumn(BoardInspectorHost, 0);
            BoardInspectorHost.HorizontalAlignment = HorizontalAlignment.Right;
            BoardInspectorHost.Margin = new Thickness(0);
            KeyboardNavigation.SetTabNavigation(BoardInspectorHost, KeyboardNavigationMode.Cycle);
            BoardInspectorScrim.Visibility = nextOverlayActive ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            BoardInspectorColumn.Width = GridLength.Auto;
            Grid.SetColumn(BoardInspectorHost, 1);
            BoardInspectorHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            BoardInspectorHost.Margin = new Thickness(10, 0, 0, 0);
            KeyboardNavigation.SetTabNavigation(BoardInspectorHost, KeyboardNavigationMode.Continue);
            BoardInspectorScrim.Visibility = Visibility.Collapsed;
        }

        if (nextOverlayActive && !_overlayActive)
        {
            _overlayActive = true;
            _focusBeforeOverlay = Keyboard.FocusedElement;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_overlayActive && !BoardInspectorHost.IsKeyboardFocusWithin)
                {
                    BoardInspectorHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }
            });
        }
        else if (!nextOverlayActive && _overlayActive)
        {
            _overlayActive = false;
            var prior = _focusBeforeOverlay;
            _focusBeforeOverlay = null;
            if (restoreFocus && prior is UIElement element && element.IsVisible)
            {
                element.Focus();
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_board is null)
        {
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_overlayActive)
            {
                _focusBeforeOverlay = null;
                _board.CloseInspectorCommand.Execute(null);
                ApplyInspectorPlacement(restoreFocus: false);
            }

            BoardFindBox.Focus();
            BoardFindBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            var command = Keyboard.Modifiers == ModifierKeys.Shift
                ? _board.PreviousSearchMatchCommand
                : _board.NextSearchMatchCommand;
            if (!command.CanExecute(null))
            {
                return;
            }

            if (_overlayActive)
            {
                _focusBeforeOverlay = null;
                _board.CloseInspectorCommand.Execute(null);
                ApplyInspectorPlacement(restoreFocus: false);
            }

            command.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.U && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _board.ToggleTrayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _board.ToggleInspectorCommand.Execute(null);
            e.Handled = true;
        }
        else if ((e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox)
                 || (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            if (_board.BeginInlineRenameCommand.CanExecute(null))
            {
                _board.BeginInlineRenameCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && _board.IsRenamingInline)
        {
            _board.CancelInlineRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _board.InspectorOpen
                 && (_isCompactWidth || BoardInspectorHost.IsKeyboardFocusWithin))
        {
            _board.CloseInspectorCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBoardFindKeyDown(object sender, KeyEventArgs e)
    {
        if (_board is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_board.NextSearchMatchCommand.CanExecute(null))
            {
                if (_overlayActive)
                {
                    _board.CloseInspectorCommand.Execute(null);
                    ApplyInspectorPlacement(restoreFocus: false);
                }

                _board.NextSearchMatchCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _board.ClearBoardSearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnInlineRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (_board is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _board.CommitInlineRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _board.CancelInlineRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTierNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _board?.EditTierCommand.CanExecute(null) == true)
        {
            _board.EditTierCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBoardMoreClick(object sender, RoutedEventArgs e)
    {
        if (BoardMoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = BoardMoreButton;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnBoardScrimClick(object sender, MouseButtonEventArgs e)
    {
        _board?.CloseInspectorCommand.Execute(null);
        ApplyInspectorPlacement();
    }

    /// <summary>After a move, focus the show in its new row (including virtualized unranked tray) so keyboard users can keep going.</summary>
    private void OnSelectionMoved(object? sender, BoardEntryViewModel entry) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            foreach (var list in Descendants<ListBox>(this))
            {
                if (!list.Items.Contains(entry))
                {
                    continue;
                }

                list.ScrollIntoView(entry);
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem item)
                {
                    item.BringIntoView();
                    // Keep keyboard focus inside the compact overlay drawer when moving shows via inspector buttons;
                    // for header search / external navigation, dismiss the overlay first so focus never lands behind the scrim.
                    if (_overlayActive && BoardInspectorHost.IsKeyboardFocusWithin)
                    {
                        return;
                    }

                    if (_overlayActive && _board is not null)
                    {
                        _board.CloseInspectorCommand.Execute(null);
                        ApplyInspectorPlacement(restoreFocus: false);
                    }

                    item.Focus();

                    return;
                }
            }
        });

    /// <summary>
    /// Selection follows keyboard focus: Tab into a row focuses a show without selecting it, and the tier keys act on
    /// the selection, so the focused show is selected.
    /// </summary>
    private void OnEntryGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ((ListBoxItem)sender).IsSelected = true;

    private void OnEntryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || _board?.Project.EditsSuspended == true)
        {
            _dragEntry = null;
            _dragVisual = null;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragVisual = ItemAt(e.OriginalSource as DependencyObject);
        _dragEntry = _dragVisual?.DataContext as BoardEntryViewModel;
    }

    /// <summary>Opens the LiveChart details and duplication dialog when a board anime card is double-clicked.</summary>
    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBoxItem { DataContext: BoardEntryViewModel entry })
        {
            return;
        }

        _dragEntry = null;
        _dragVisual = null;
        _board?.OpenEntryDetailsCommand.Execute(entry);
        e.Handled = true;
    }

    private void OnEntryMouseMove(object sender, MouseEventArgs e)
    {
        if (_board?.Project.EditsSuspended == true || _dragEntry is null || _dragVisual is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var entry = _dragEntry;
        var visual = _dragVisual;
        _dragEntry = null;
        _dragVisual = null;
        visual.Opacity = 0.35;
        StartGhostAdorner(visual, e.GetPosition(this));
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(BoardEntryViewModel), entry), DragDropEffects.Move);
        }
        finally
        {
            visual.Opacity = 1.0;
            ClearAllDragFeedback();
        }
    }

    private void OnTierHeaderDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(BoardEntryViewModel)) || sender is not FrameworkElement el || el.DataContext is not TierViewModel tier)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        UpdateGhostPosition(e);
        HighlightTier(tier);
        RemoveCaretAdorner();
        if (_board is not null)
        {
            _board.DragStatusText = tier.IsPool ? "Return to Unranked (restores collection order)" : $"Move to tier {tier.Name} (at end)";
        }

        e.Handled = true;
    }

    private void OnTierHeaderDrop(object sender, DragEventArgs e)
    {
        ClearAllDragFeedback();
        if (_board is null || sender is not FrameworkElement el || el.DataContext is not TierViewModel tier || e.Data.GetData(typeof(BoardEntryViewModel)) is not BoardEntryViewModel entry)
        {
            return;
        }

        _board.Drop(entry, tier, tier.Entries.Count);
        e.Handled = true;
    }

    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(BoardEntryViewModel)) || sender is not ListBox list || list.DataContext is not TierViewModel tier)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        UpdateGhostPosition(e);
        HighlightTier(tier);

        if (tier.IsPool)
        {
            // Unranked has no manual order; highlight the whole tray with no insertion caret.
            EnsureCaretAdorner(list).UpdateCaret(0, 0, 0, showCaret: false);
            if (_board is not null)
            {
                _board.DragStatusText = "Return to Unranked (restores original collection seed order)";
            }
        }
        else
        {
            var bounds = CollectBounds(list);
            var index = BoardDropCalculator.ComputeIndex(bounds, e.GetPosition(list));
            var (x, top, bottom) = BoardDropCalculator.ComputeCaret(bounds, index, list.ActualHeight);
            EnsureCaretAdorner(list).UpdateCaret(x, top, bottom, showCaret: true);
            if (_board is not null)
            {
                _board.DragStatusText = DescribeDropTarget(tier, index);
            }
        }

        e.Handled = true;
    }

    private void OnRowDragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBox list && ReferenceEquals(list, _caretTargetList))
        {
            RemoveCaretAdorner();
        }
    }

    private void OnRowDrop(object sender, DragEventArgs e)
    {
        ClearAllDragFeedback();
        if (_board is null || sender is not ListBox list || list.DataContext is not TierViewModel tier || e.Data.GetData(typeof(BoardEntryViewModel)) is not BoardEntryViewModel entry)
        {
            return;
        }

        var index = tier.IsPool ? 0 : BoardDropCalculator.ComputeIndex(CollectBounds(list), e.GetPosition(list));
        _board.Drop(entry, tier, index);
        e.Handled = true;
    }

    /// <summary>While dragging near the top or bottom edge, scroll so any row can be reached.</summary>
    private void OnRowsDragOver(object sender, DragEventArgs e)
    {
        UpdateGhostPosition(e);
        const double edge = 48;
        var y = e.GetPosition(Rows).Y;
        if (y < edge)
        {
            Rows.ScrollToVerticalOffset(Rows.VerticalOffset - (edge - y));
        }
        else if (y > Rows.ActualHeight - edge)
        {
            Rows.ScrollToVerticalOffset(Rows.VerticalOffset + (y - (Rows.ActualHeight - edge)));
        }
    }

    /// <summary>Tier rows do not scroll themselves; pass the wheel to the board's scroll viewer.</summary>
    private void OnRowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Rows.ScrollToVerticalOffset(Rows.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void StartGhostAdorner(UIElement draggedVisual, Point startPosition)
    {
        var layer = AdornerLayer.GetAdornerLayer(this);
        if (layer is null)
        {
            return;
        }

        _ghostAdorner = new DragPreviewAdorner(this, draggedVisual, startPosition);
        layer.Add(_ghostAdorner);
    }

    private void UpdateGhostPosition(DragEventArgs e)
    {
        _ghostAdorner?.UpdatePosition(e.GetPosition(this));
    }

    private void HighlightTier(TierViewModel tier)
    {
        if (ReferenceEquals(_highlightedTier, tier))
        {
            return;
        }

        if (_highlightedTier is not null)
        {
            _highlightedTier.IsDragTarget = false;
        }

        _highlightedTier = tier;
        _highlightedTier.IsDragTarget = true;
    }

    private DropInsertionAdorner EnsureCaretAdorner(ListBox list)
    {
        if (_caretAdorner is not null && ReferenceEquals(_caretTargetList, list))
        {
            return _caretAdorner;
        }

        RemoveCaretAdorner();
        _caretTargetList = list;
        _caretAdorner = new DropInsertionAdorner(list);
        AdornerLayer.GetAdornerLayer(list)?.Add(_caretAdorner);
        return _caretAdorner;
    }

    private void RemoveCaretAdorner()
    {
        if (_caretAdorner is not null)
        {
            AdornerLayer.GetAdornerLayer(_caretAdorner.AdornedElement)?.Remove(_caretAdorner);
            _caretAdorner = null;
            _caretTargetList = null;
        }
    }

    private void ClearAllDragFeedback()
    {
        RemoveCaretAdorner();
        if (_ghostAdorner is not null)
        {
            AdornerLayer.GetAdornerLayer(_ghostAdorner.AdornedElement)?.Remove(_ghostAdorner);
            _ghostAdorner = null;
        }

        if (_highlightedTier is not null)
        {
            _highlightedTier.IsDragTarget = false;
            _highlightedTier = null;
        }

        if (_board is not null)
        {
            _board.DragStatusText = string.Empty;
        }
    }

    private static string DescribeDropTarget(TierViewModel tier, int index)
    {
        if (tier.Entries.Count == 0)
        {
            return $"Move to tier {tier.Name}";
        }

        if (index <= 0)
        {
            return $"Move to tier {tier.Name}, before {tier.Entries[0].Title}";
        }

        if (index >= tier.Entries.Count)
        {
            return $"Move to tier {tier.Name}, after {tier.Entries[^1].Title}";
        }

        return $"Move to tier {tier.Name}, before {tier.Entries[index].Title}";
    }

    private static List<Rect> CollectBounds(ListBox list)
    {
        var bounds = new List<Rect>(list.Items.Count);
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
            {
                var topLeft = item.TranslatePoint(new Point(0, 0), list);
                bounds.Add(new Rect(topLeft, item.RenderSize));
            }
        }

        return bounds;
    }

    private static ListBoxItem? ItemAt(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
