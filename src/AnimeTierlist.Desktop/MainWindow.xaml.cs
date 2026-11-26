using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AnimeTierlist.Desktop.Controls;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop;

/// <summary>Top-level desktop window: handles safe closing with failed-save recovery, responsive sidebar sizing, and sidebar drag-reorder.</summary>
public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _closeInProgress;
    private bool _autoCollapsedSidebar;
    private bool _userExpandedInCompact;
    private bool _updatingAutoCollapse;
    private ProjectViewModel? _attachedProject;
    private Point _sidebarDragStart;
    private object? _sidebarDragItem;
    private ListBox? _sidebarDragSource;
    private HorizontalDropInsertionAdorner? _sidebarAdorner;
    private ListBox? _sidebarAdornerList;

    public MainWindow()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnWindowLoaded;
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private void OnWindowLoaded(object sender, RoutedEventArgs e) =>
        EvaluateResponsiveSidebar(ActualWidth > 0 ? ActualWidth : Width);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ShellViewModel oldShell)
        {
            oldShell.PropertyChanged -= OnShellPropertyChanged;
        }

        if (e.NewValue is ShellViewModel newShell)
        {
            newShell.PropertyChanged += OnShellPropertyChanged;
            AttachProjectListeners();
            EvaluateResponsiveSidebar(ActualWidth > 0 ? ActualWidth : Width);
        }
        else
        {
            AttachProjectListeners();
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Project))
        {
            // Layers show the previous project's data; close them with it.
            Overlays.CloseAll();
            _autoCollapsedSidebar = false;
            _userExpandedInCompact = false;
            AttachProjectListeners();
            EvaluateResponsiveSidebar(ActualWidth > 0 ? ActualWidth : Width);
        }
    }

    private void AttachProjectListeners()
    {
        if (_attachedProject is not null)
        {
            _attachedProject.PropertyChanged -= OnProjectPropertyChanged;
        }

        _attachedProject = Shell?.Project;
        if (_attachedProject is not null)
        {
            _attachedProject.PropertyChanged += OnProjectPropertyChanged;
        }
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectViewModel.SidebarCollapsed))
        {
            if (!_updatingAutoCollapse && (ActualWidth > 0 ? ActualWidth : Width) < 1020)
            {
                _userExpandedInCompact = _attachedProject is { SidebarCollapsed: false };
                _autoCollapsedSidebar = false;
            }

            SyncSidebarColumn();
        }
        else if (e.PropertyName == nameof(ProjectViewModel.SidebarWidth))
        {
            SyncSidebarColumn();
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) =>
        EvaluateResponsiveSidebar(e.NewSize.Width);

    /// <summary>Routes Alt+I and Alt+X window-wide when a collection is active so compact widths match wide AccessKey reach.</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Overlays.IsOpen)
        {
            e.Handled = HandleOverlayKey(e);
            return;
        }

        if (TryHandleCollectionAccessKey(Shell, Keyboard.Modifiers, e.Key, e.SystemKey))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// While a layer is open, Esc closes it, and no key reaches the content behind it: keys aimed outside the layer
    /// (for example a board card that kept focus) move focus into the layer instead, and the window's own shortcuts
    /// (undo, redo, sidebar, help) do nothing.
    /// </summary>
    private bool HandleOverlayKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            return Overlays.HandleEscape();
        }

        if (!IsInsideOverlay(e.OriginalSource as DependencyObject))
        {
            Overlays.FocusTop();
            // Alt+F4 still closes the window.
            return !(e.Key == Key.System && e.SystemKey == Key.F4);
        }

        return InputBindings.OfType<InputBinding>().Any(binding => binding.Gesture?.Matches(this, e) == true);
    }

    private bool IsInsideOverlay(DependencyObject? element)
    {
        while (element is not null && !ReferenceEquals(element, Overlays))
        {
            element = (element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : null)
                ?? LogicalTreeHelper.GetParent(element);
        }

        return element is not null;
    }

    /// <summary>Executes collection-wide Include All (Alt+I) or Exclude All (Alt+X) when exact Alt is pressed.</summary>
    internal static bool TryHandleCollectionAccessKey(ShellViewModel? shell, ModifierKeys modifiers, Key key, Key systemKey)
    {
        if (modifiers != ModifierKeys.Alt || shell?.Project?.Content is not CollectionViewModel collection)
        {
            return false;
        }

        var effectiveKey = key == Key.System ? systemKey : key;
        if (effectiveKey == Key.I && collection.IncludeAllCommand.CanExecute(null))
        {
            collection.IncludeAllCommand.Execute(null);
            return true;
        }

        if (effectiveKey == Key.X && collection.ExcludeAllCommand.CanExecute(null))
        {
            collection.ExcludeAllCommand.Execute(null);
            return true;
        }

        return false;
    }

    private void EvaluateResponsiveSidebar(double width)
    {
        if (Shell?.Project is not { } project)
        {
            SyncSidebarColumn();
            return;
        }

        // At compact widths (e.g. 960x540 DIP), collapse the sidebar automatically unless the user explicitly expanded it.
        if (width > 0 && width < 1020 && !project.SidebarCollapsed && !_userExpandedInCompact)
        {
            _updatingAutoCollapse = true;
            try
            {
                _autoCollapsedSidebar = true;
                project.SidebarCollapsed = true;
            }
            finally
            {
                _updatingAutoCollapse = false;
            }
        }
        else if (width >= 1060)
        {
            _userExpandedInCompact = false;
            if (_autoCollapsedSidebar && project.SidebarCollapsed)
            {
                _updatingAutoCollapse = true;
                try
                {
                    _autoCollapsedSidebar = false;
                    project.SidebarCollapsed = false;
                }
                finally
                {
                    _updatingAutoCollapse = false;
                }
            }
        }

        SyncSidebarColumn();
    }

    private void SyncSidebarColumn()
    {
        if (Shell?.Project is not { } project || project.SidebarCollapsed)
        {
            SidebarColumn.Width = new GridLength(0);
            SidebarColumn.MinWidth = 0;
        }
        else
        {
            SidebarColumn.MinWidth = 180;
            SidebarColumn.Width = new GridLength(Math.Clamp(project.SidebarWidth, 180, 340));
        }
    }

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        // Track manual toggle explicitly when clicked via button (Ctrl+B is also caught in OnProjectPropertyChanged).
        if ((ActualWidth > 0 ? ActualWidth : Width) < 1020)
        {
            _autoCollapsedSidebar = false;
        }
    }

    private void OnSidebarSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (Shell?.Project is { SidebarCollapsed: false } project && SidebarColumn.ActualWidth >= 180)
        {
            project.SidebarWidth = Math.Clamp(SidebarColumn.ActualWidth, 180, 340);
        }
    }

    private void OnShellMoreClick(object sender, RoutedEventArgs e)
    {
        if (ShellMoreButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = ShellMoreButton;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    /// <summary>Arms a sidebar collection or board item for drag-reordering on single left-button press.</summary>
    private void OnSidebarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || Shell?.Project is not { EditsEnabled: true } || sender is not ListBox list)
        {
            _sidebarDragItem = null;
            _sidebarDragSource = null;
            return;
        }

        _sidebarDragStart = e.GetPosition(this);
        var container = SidebarItemAt(e.OriginalSource as DependencyObject);
        _sidebarDragItem = container?.DataContext;
        _sidebarDragSource = _sidebarDragItem is not null ? list : null;
    }

    /// <summary>Starts WPF drag-and-drop once the pointer moves beyond the system drag threshold.</summary>
    private void OnSidebarMouseMove(object sender, MouseEventArgs e)
    {
        if (Shell?.Project is not { EditsEnabled: true } || _sidebarDragItem is null || _sidebarDragSource is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _sidebarDragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _sidebarDragItem;
        var source = _sidebarDragSource;
        _sidebarDragItem = null;
        _sidebarDragSource = null;
        try
        {
            DragDrop.DoDragDrop(source, new DataObject(item.GetType(), item), DragDropEffects.Move);
        }
        finally
        {
            RemoveSidebarAdorner();
        }
    }

    /// <summary>Shows the horizontal drop insertion indicator when dragging within the matching sidebar list.</summary>
    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        if (Shell?.Project is not { EditsEnabled: true } || sender is not ListBox list || !IsMatchingSidebarDrag(list, e.Data))
        {
            e.Effects = DragDropEffects.None;
            RemoveSidebarAdorner();
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var (_, y, left, right) = BoardDropCalculator.ComputeVerticalDrop(CollectSidebarItems(list), list.Items.Count, e.GetPosition(list), list.ActualWidth);
        EnsureSidebarAdorner(list).UpdateLine(y, left, right);
        e.Handled = true;
    }

    /// <summary>Clears the sidebar insertion indicator when the pointer leaves the list.</summary>
    private void OnSidebarDragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBox list && ReferenceEquals(list, _sidebarAdornerList))
        {
            RemoveSidebarAdorner();
        }
    }

    /// <summary>Commits the new collection or board order on drop as one undoable project command.</summary>
    private void OnSidebarDrop(object sender, DragEventArgs e)
    {
        RemoveSidebarAdorner();
        if (Shell?.Project is not { EditsEnabled: true } project || sender is not ListBox list)
        {
            return;
        }

        var (index, _, _, _) = BoardDropCalculator.ComputeVerticalDrop(CollectSidebarItems(list), list.Items.Count, e.GetPosition(list), list.ActualWidth);
        if (ReferenceEquals(list, CollectionsList) && e.Data.GetData(typeof(CollectionItem)) is CollectionItem collection)
        {
            project.MoveCollection(collection, index);
            e.Handled = true;
        }
        else if (ReferenceEquals(list, BoardsList) && e.Data.GetData(typeof(BoardItem)) is BoardItem board)
        {
            project.MoveBoard(board, index);
            e.Handled = true;
        }
    }

    private bool IsMatchingSidebarDrag(ListBox list, IDataObject data) =>
        (ReferenceEquals(list, CollectionsList) && data.GetDataPresent(typeof(CollectionItem)))
        || (ReferenceEquals(list, BoardsList) && data.GetDataPresent(typeof(BoardItem)));

    private HorizontalDropInsertionAdorner EnsureSidebarAdorner(ListBox list)
    {
        if (_sidebarAdorner is not null && ReferenceEquals(_sidebarAdornerList, list))
        {
            return _sidebarAdorner;
        }

        RemoveSidebarAdorner();
        _sidebarAdornerList = list;
        _sidebarAdorner = new HorizontalDropInsertionAdorner(list);
        AdornerLayer.GetAdornerLayer(list)?.Add(_sidebarAdorner);
        return _sidebarAdorner;
    }

    private void RemoveSidebarAdorner()
    {
        if (_sidebarAdorner is not null)
        {
            AdornerLayer.GetAdornerLayer(_sidebarAdorner.AdornedElement)?.Remove(_sidebarAdorner);
            _sidebarAdorner = null;
            _sidebarAdornerList = null;
        }
    }

    /// <summary>Collects each visible realized container's actual list index and bounds in <paramref name="list"/> coordinates.</summary>
    internal static List<BoardDropCalculator.IndexedRect> CollectSidebarItems(ListBox list)
    {
        var items = new List<BoardDropCalculator.IndexedRect>(list.Items.Count);
        var viewportHeight = list.ActualHeight > 0 ? list.ActualHeight : double.PositiveInfinity;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item && item.Visibility == Visibility.Visible && item.RenderSize.Height > 0)
            {
                var topLeft = item.TranslatePoint(new Point(0, 0), list);
                var rect = new Rect(topLeft, item.RenderSize);
                if (rect.Bottom > 0 && rect.Top < viewportHeight)
                {
                    items.Add(new BoardDropCalculator.IndexedRect(i, rect));
                }
            }
        }

        return items;
    }

    private static ListBoxItem? SidebarItemAt(DependencyObject? element)
    {
        while (element is not null and not ListBoxItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }

    /// <summary>
    /// Waits for queued saves before closing; if saving failed, offers Retry, Discard, or Cancel directly
    /// routed through the shared shell recovery workflow.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Stop video playback and release the details view model before the project closes.
        Overlays.CloseAll();
        if (_closeConfirmed || Shell is not { } shell)
        {
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            if (await shell.CloseProjectWithRecoveryAsync())
            {
                // The awaited work may finish synchronously, inside this Closing event, where WPF refuses a nested
                // Close. Queue the real close for after the event returns.
                _closeConfirmed = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
        catch (Exception ex)
        {
            shell.Services.Dialogs?.Inform("AnimeTierlist", $"The project could not be closed cleanly: {ex.Message}");
        }
        finally
        {
            _closeInProgress = false;
        }
    }
}
