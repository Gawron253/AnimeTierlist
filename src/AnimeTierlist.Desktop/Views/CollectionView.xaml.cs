using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AnimeTierlist.Desktop.Controls;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>
/// View mechanics for collection curation: responsive inspector overlay with focus restoration,
/// Ctrl+F search focus, F2 custom-title / Ctrl+Shift+R rename, Space batch inclusion toggle,
/// and unfiltered drag-to-reorder with insertion caret and edge auto-scroll.
/// </summary>
public partial class CollectionView : UserControl
{
    private Point _dragStart;
    private EntryViewModel? _dragEntry;
    private DropInsertionAdorner? _dropAdorner;
    private CollectionViewModel? _attachedViewModel;
    private IInputElement? _focusBeforeOverlay;
    private bool _isCompactWidth;
    private bool _overlayActive;

    public CollectionView()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private CollectionViewModel? ViewModel => DataContext as CollectionViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isCompactWidth = ActualWidth > 0 && ActualWidth < 960;
        if (_isCompactWidth && _attachedViewModel is { InspectorOpen: true } compactVm)
        {
            compactVm.InspectorOpen = false;
        }

        _attachedViewModel?.SetDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleY);
        SyncCompactViewportDensity();
        ApplyInspectorPlacement();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ViewModel?.SetDpiScale(newDpi.DpiScaleY);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _attachedViewModel.EntryMoved -= OnEntryMoved;
            _attachedViewModel.FilterOrSearchChanged -= OnFilterOrSearchChanged;
        }

        _attachedViewModel = DataContext as CollectionViewModel;
        if (_attachedViewModel is not null)
        {
            _attachedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _attachedViewModel.EntryMoved += OnEntryMoved;
            _attachedViewModel.FilterOrSearchChanged += OnFilterOrSearchChanged;
        }

        if (_isCompactWidth && _attachedViewModel is { InspectorOpen: true } compactVm)
        {
            compactVm.InspectorOpen = false;
        }

        _attachedViewModel?.SetDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleY);
        SyncCompactViewportDensity();
        ApplyInspectorPlacement();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionViewModel.InspectorOpen))
        {
            ApplyInspectorPlacement();
        }
        else if (e.PropertyName == nameof(CollectionViewModel.IsRenamingInline) && ViewModel?.IsRenamingInline == true)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                InlineRenameBox.Focus();
                InlineRenameBox.SelectAll();
            });
        }
    }

    private void OnEntryMoved(object? sender, EntryViewModel entry) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            ListBox activeList = ViewModel?.IsListView == true ? EntryList : PosterGrid;
            activeList.ScrollIntoView(entry);
            activeList.UpdateLayout();
            if (activeList.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem container)
            {
                container.BringIntoView();
                // Keep keyboard focus inside the overlay drawer when triggered from inspector reorder buttons.
                if (!(_overlayActive && InspectorHost.IsKeyboardFocusWithin))
                {
                    container.Focus();
                }

                if (container is PosterGridListBoxItem posterItem)
                {
                    posterItem.InvalidateItemAutomationChildren();
                }
            }
        });

    private void OnFilterOrSearchChanged(object? sender, EventArgs e)
    {
        ListBox activeList = ViewModel?.IsListView == true ? EntryList : PosterGrid;
        if (FindDescendant<ScrollViewer>(activeList) is { } scroll)
        {
            scroll.ScrollToTop();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 960;
        if (compact != _isCompactWidth)
        {
            _isCompactWidth = compact;
            if (compact && ViewModel is { InspectorOpen: true } vm)
            {
                vm.InspectorOpen = false;
            }

            ApplyInspectorPlacement();
        }

        SyncCompactViewportDensity();
    }

    private void OnWorkspaceGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SyncCompactViewportDensity();
        SyncListTitleColumnWidth();
    }

    private void OnEntryListSizeChanged(object sender, SizeChangedEventArgs e) => SyncListTitleColumnWidth();

    private void SyncListTitleColumnWidth()
    {
        if (EntryList.ActualWidth <= 0 || EntryList.View is not GridView gridView)
        {
            return;
        }

        var fixedWidth = 0.0;
        foreach (var column in gridView.Columns)
        {
            if (!ReferenceEquals(column, ListTitleColumn))
            {
                fixedWidth += double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
            }
        }

        var scrollViewer = FindDescendant<ScrollViewer>(EntryList);
        var viewportWidth = scrollViewer is { ViewportWidth: > 0 }
            ? scrollViewer.ViewportWidth
            : EntryList.ActualWidth - SystemParameters.VerticalScrollBarWidth - 4;
        // Account for ListViewItem horizontal padding, Border, and GridView header/presenter chrome so horizontal scrolling never triggers.
        const double itemAndHeaderChrome = 24;
        ListTitleColumn.Width = Math.Max(220, Math.Floor(viewportWidth - fixedWidth - itemAndHeaderChrome));
    }

    private void SyncCompactViewportDensity()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        vm.IsNarrowToolbar = ActualWidth > 0 && ActualWidth < 960;
        var availableGridHeight = WorkspaceGrid.ActualHeight;
        vm.CompactViewportLevel = availableGridHeight > 0 && availableGridHeight < 225
            ? 2
            : availableGridHeight > 0 && availableGridHeight < 260
                ? 1
                : 0;
    }

    /// <summary>
    /// Synchronizes docked vs overlay inspector placement and centralizes focus capture/restoration
    /// across load, resize, and open/close transitions so focus is never stranded behind the scrim.
    /// </summary>
    private void ApplyInspectorPlacement(bool restoreFocus = true)
    {
        var nextOverlayActive = _isCompactWidth && ViewModel?.InspectorOpen == true;
        if (_isCompactWidth)
        {
            // Overlay drawer above the poster grid so 960x540 DIP keeps full poster width.
            InspectorColumn.Width = new GridLength(0);
            Grid.SetColumn(InspectorHost, 0);
            InspectorHost.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorHost.Margin = new Thickness(0);
            KeyboardNavigation.SetTabNavigation(InspectorHost, KeyboardNavigationMode.Cycle);
            InspectorScrim.Visibility = nextOverlayActive ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            InspectorColumn.Width = GridLength.Auto;
            Grid.SetColumn(InspectorHost, 1);
            InspectorHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorHost.Margin = new Thickness(10, 0, 0, 0);
            KeyboardNavigation.SetTabNavigation(InspectorHost, KeyboardNavigationMode.Continue);
            InspectorScrim.Visibility = Visibility.Collapsed;
        }

        if (nextOverlayActive && !_overlayActive)
        {
            _overlayActive = true;
            _focusBeforeOverlay = Keyboard.FocusedElement;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_overlayActive && !InspectorHost.IsKeyboardFocusWithin)
                {
                    InspectorHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_overlayActive && ViewModel is not null)
            {
                _focusBeforeOverlay = null;
                ViewModel.CloseInspectorCommand.Execute(null);
                ApplyInspectorPlacement(restoreFocus: false);
            }

            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (ViewModel?.BeginInlineRenameCommand.CanExecute(null) == true)
            {
                ViewModel.BeginInlineRenameCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F4 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ViewModel?.ToggleInspectorCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.I || e.Key == Key.I))
        {
            if (ViewModel?.IncludeAllCommand.CanExecute(null) == true)
            {
                ViewModel.IncludeAllCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.X || e.Key == Key.X))
        {
            if (ViewModel?.ExcludeAllCommand.CanExecute(null) == true)
            {
                ViewModel.ExcludeAllCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is not TextBox)
        {
            if (ViewModel?.SelectedEntry is not null && ViewModel.Project.EditsEnabled)
            {
                if (!ViewModel.InspectorOpen)
                {
                    ViewModel.ToggleInspectorCommand.Execute(null);
                }

                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    OverrideBox.Focus();
                    OverrideBox.SelectAll();
                });
                e.Handled = true;
            }
            else if (ViewModel?.BeginInlineRenameCommand.CanExecute(null) == true)
            {
                ViewModel.BeginInlineRenameCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && ViewModel is { IsRenamingInline: true })
        {
            ViewModel.CancelInlineRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && ViewModel is { InspectorOpen: true }
                 && (_isCompactWidth || InspectorHost.IsKeyboardFocusWithin))
        {
            ViewModel.CloseInspectorCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is not null)
        {
            ViewModel.SearchText = string.Empty;
            e.Handled = true;
        }
    }

    private void OnInlineRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ViewModel.CommitInlineRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel.CancelInlineRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnOverrideKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel?.SetTitleOverrideCommand.CanExecute(null) == true)
        {
            ViewModel.SetTitleOverrideCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCollectionMoreClick(object sender, RoutedEventArgs e)
    {
        if (CollectionMoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = CollectionMoreButton;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.CloseInspectorCommand.Execute(null);
        ApplyInspectorPlacement();
    }

    private void OnEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { SelectedEntry: not null } vm && !vm.InspectorOpen)
        {
            vm.ToggleInspectorCommand.Execute(null);
            ApplyInspectorPlacement();
        }
    }

    /// <summary>
    /// Selection follows keyboard focus when nothing is selected yet, preserving Shift/Ctrl extended multi-selection
    /// while ensuring Tab into an unselected grid or list immediately enables Space and Alt+Up/Down.
    /// </summary>
    private void OnEntryGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBoxItem item
            && Keyboard.Modifiers == ModifierKeys.None
            && ItemsControl.ItemsControlFromItemContainer(item) is ListBox list
            && list.SelectedItems.Count == 0)
        {
            item.IsSelected = true;
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && sender is ListBox list && ViewModel is not null)
        {
            var selected = list.SelectedItems.OfType<EntryViewModel>().ToList();
            if (selected.Count > 0)
            {
                ViewModel.ToggleEntries(selected);
                e.Handled = true;
            }
        }
        else if ((e.Key == Key.Home || e.Key == Key.End) && Keyboard.Modifiers == ModifierKeys.None && sender is ListBox navList && navList.Items.Count > 0)
        {
            var targetIndex = e.Key == Key.Home ? 0 : navList.Items.Count - 1;
            var targetItem = navList.Items[targetIndex];
            navList.SelectedItems.Clear();
            navList.SelectedItem = targetItem;
            navList.ScrollIntoView(targetItem);
            navList.UpdateLayout();
            if (navList.ItemContainerGenerator.ContainerFromIndex(targetIndex) is ListBoxItem container)
            {
                container.BringIntoView();
                container.Focus();
                if (container is PosterGridListBoxItem posterItem)
                {
                    posterItem.InvalidateItemAutomationChildren();
                }
            }

            e.Handled = true;
        }
    }

    private void OnCollectionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { CanReorder: true } || e.OriginalSource is DependencyObject src && IsInsideCheckBox(src))
        {
            _dragEntry = null;
            return;
        }

        _dragStart = e.GetPosition(this);
        _dragEntry = ItemAt(e.OriginalSource as DependencyObject)?.DataContext as EntryViewModel;
    }

    private void OnCollectionMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry is null || e.LeftButton != MouseButtonState.Pressed || ViewModel is not { CanReorder: true })
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var entry = _dragEntry;
        _dragEntry = null;
        try
        {
            DragDrop.DoDragDrop(PosterGrid, new DataObject(typeof(EntryViewModel), entry), DragDropEffects.Move);
        }
        finally
        {
            RemoveDropAdorner();
        }
    }

    private void OnCollectionDragOver(object sender, DragEventArgs e)
    {
        if (ViewModel is not { CanReorder: true } || !e.Data.GetDataPresent(typeof(EntryViewModel)))
        {
            e.Effects = DragDropEffects.None;
            RemoveDropAdorner();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var pointer = e.GetPosition(PosterGrid);
        AutoScrollGrid(PosterGrid, pointer);
        var (bounds, items) = CollectVisibleCardBounds(PosterGrid);
        var localIndex = BoardDropCalculator.ComputeIndex(bounds, pointer);
        var (x, top, bottom) = BoardDropCalculator.ComputeCaret(bounds, localIndex, PosterGrid.ActualHeight);
        EnsureDropAdorner(PosterGrid).UpdateCaret(x, top, bottom, bounds.Count > 0);
        e.Handled = true;
    }

    private static void AutoScrollGrid(ListBox list, Point pointer)
    {
        if (FindDescendant<ScrollViewer>(list) is not { } viewer)
        {
            return;
        }

        const double edge = 42;
        if (pointer.Y < edge)
        {
            viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset - (edge - pointer.Y) * 0.6));
        }
        else if (pointer.Y > list.ActualHeight - edge)
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (pointer.Y - (list.ActualHeight - edge)) * 0.6);
        }
    }

    private void OnCollectionDragLeave(object sender, DragEventArgs e) => RemoveDropAdorner();

    private void OnCollectionDrop(object sender, DragEventArgs e)
    {
        RemoveDropAdorner();
        if (ViewModel is not { CanReorder: true } vm || e.Data.GetData(typeof(EntryViewModel)) is not EntryViewModel entry)
        {
            return;
        }

        var (bounds, items) = CollectVisibleCardBounds(PosterGrid);
        var localIndex = BoardDropCalculator.ComputeIndex(bounds, e.GetPosition(PosterGrid));
        int targetIndex;
        if (items.Count == 0)
        {
            targetIndex = vm.Entries.Count;
        }
        else if (localIndex >= items.Count)
        {
            targetIndex = vm.Entries.IndexOf(items[^1]) + 1;
        }
        else
        {
            targetIndex = vm.Entries.IndexOf(items[localIndex]);
        }

        vm.MoveEntryTo(entry, Math.Max(0, targetIndex));
        e.Handled = true;
    }

    private DropInsertionAdorner EnsureDropAdorner(UIElement target)
    {
        if (_dropAdorner is not null)
        {
            return _dropAdorner;
        }

        _dropAdorner = new DropInsertionAdorner(target);
        AdornerLayer.GetAdornerLayer(target)?.Add(_dropAdorner);
        return _dropAdorner;
    }

    private void RemoveDropAdorner()
    {
        if (_dropAdorner is null)
        {
            return;
        }

        AdornerLayer.GetAdornerLayer(_dropAdorner.AdornedElement)?.Remove(_dropAdorner);
        _dropAdorner = null;
    }

    private static (List<Rect> Bounds, List<EntryViewModel> Items) CollectVisibleCardBounds(ListBox list)
    {
        var bounds = new List<Rect>();
        var items = new List<EntryViewModel>();
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container
                && container.IsVisible
                && container.DataContext is EntryViewModel vm)
            {
                var topLeft = container.TranslatePoint(new Point(0, 0), list);
                bounds.Add(new Rect(topLeft, container.RenderSize));
                items.Add(vm);
            }
        }

        return (bounds, items);
    }

    private static bool IsInsideCheckBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is CheckBox)
            {
                return true;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private static ListBoxItem? ItemAt(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }
}
