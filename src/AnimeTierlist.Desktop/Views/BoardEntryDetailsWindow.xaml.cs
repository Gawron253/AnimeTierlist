using System.Windows;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Views;

/// <summary>Themed modal dialog showing LiveChart anime details, OP/ED video links, AniList/MyAnimeList links, and duplication controls.</summary>
public partial class BoardEntryDetailsWindow : Window
{
    private BoardEntryDetailsViewModel? _attached;

    public BoardEntryDetailsWindow()
    {
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
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
        if (_attached is not null)
        {
            _ = _attached.EnsureLoadedAsync();
        }
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_attached is not null)
        {
            _attached.RequestClose -= OnRequestClose;
            _attached.Dispose();
            _attached = null;
        }
    }
}
