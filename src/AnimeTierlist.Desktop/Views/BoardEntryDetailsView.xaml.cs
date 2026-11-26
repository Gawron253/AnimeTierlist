using System.Windows;
using System.Windows.Controls;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>
/// LiveChart anime details, OP/ED video links, AniList/MyAnimeList links, and duplication controls, shown as a
/// layer inside the main window. Closing the layer disposes the view model.
/// </summary>
public partial class BoardEntryDetailsView : UserControl, IOverlayView
{
    private BoardEntryDetailsViewModel? _attached;
    private bool _loadStarted;

    public BoardEntryDetailsView()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public event EventHandler? CloseRequested;

    event EventHandler? IOverlayView.FullWindowChanged
    {
        add { }
        remove { }
    }

    public bool IsFullWindow => false;

    public Size MaxCardSize => new(680, 740);

    public bool FillsHeight => false;

    public void OnOverlayClosed()
    {
        if (_attached is not null)
        {
            _attached.RequestClose -= OnRequestClose;
            _attached.Dispose();
            _attached = null;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_attached is not null)
        {
            _attached.RequestClose -= OnRequestClose;
        }

        _attached = DataContext as BoardEntryDetailsViewModel;
        if (_attached is not null)
        {
            _attached.RequestClose += OnRequestClose;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached is not null && !_loadStarted)
        {
            _loadStarted = true;
            _ = _attached.EnsureLoadedAsync();
        }
    }

    private void OnRequestClose(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
