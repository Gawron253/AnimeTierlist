using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Desktop.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace AnimeTierlist.Desktop.Views;

/// <summary>
/// Sandboxed in-app YouTube player. This code-behind owns the WebView2 mechanics only: it locks the browser
/// down, allows nothing but the packaged player page at the top level, and reports what happens to
/// <see cref="VideoPlayerViewModel"/>. It is shown as a layer inside the main window, so the browser is a child
/// window of the main window and window capture includes it. Closing the layer disposes the browser, which ends
/// its processes.
/// </summary>
public partial class VideoPlayerView : UserControl, IOverlayView
{
    private readonly VideoPlayerViewModel _player;
    private readonly VideoPlayerEnvironment? _environment;
    private readonly DispatcherTimer _readyTimer;
    private CoreWebView2? _core;
    private bool _started;
    private bool _closed;
    private bool _browserDisposed;
    private bool _fullWindow;
    private ulong? _playerNavigationId;
    private Thickness _restoreMargin;

    public VideoPlayerView(VideoPlayerViewModel player, VideoPlayerEnvironment? environment)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        _environment = environment;
        Services.ThemeManager.EnsureViewResources(this);
        InitializeComponent();
        DataContext = player;

        _readyTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = YouTubePlayerPolicy.ReadyTimeout };
        _readyTimer.Tick += OnReadyTimeout;
        _player.PropertyChanged += OnPlayerChanged;
        Loaded += OnLoaded;
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? FullWindowChanged;

    public bool IsFullWindow => _fullWindow;

    public Size MaxCardSize => new(1040, 720);

    public bool FillsHeight => true;

    // Event handlers must be async void; every exception is caught and reported to the view model.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || _closed)
        {
            return;
        }

        _started = true;
        try
        {
            await StartAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closed)
            {
                _player.Fail(YouTubePlayerFailure.StartFailed);
            }
        }
    }

    private async Task StartAsync()
    {
        if (_environment is null || !VideoPlayerEnvironment.IsRuntimeAvailable())
        {
            _player.Fail(YouTubePlayerFailure.RuntimeMissing);
            return;
        }

        var environment = await _environment.GetAsync();
        if (_closed || _player.IsUnavailable)
        {
            return;
        }

        await PlayerView.EnsureCoreWebView2Async(environment);
        if (_closed || _player.IsUnavailable)
        {
            return;
        }

        var core = PlayerView.CoreWebView2;
        _core = core;
        Harden(core.Settings);
        core.SetVirtualHostNameToFolderMapping(YouTubePlayerPolicy.PlayerHost, _environment.PlayerAssetsFolder, CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.DownloadStarting += OnDownloadStarting;
        core.PermissionRequested += OnPermissionRequested;
        core.ProcessFailed += OnProcessFailed;
        core.ContainsFullScreenElementChanged += OnFullScreenChanged;

        _readyTimer.Start();
        core.Navigate(_player.PlayerPageUrl.AbsoluteUri);
    }

    private static void Harden(CoreWebView2Settings settings)
    {
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
    }

    /// <summary>Only the packaged player page may load at the top level. A clicked video link opens externally.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && YouTubePlayerPolicy.IsPlayerPage(target))
        {
            _playerNavigationId = e.NavigationId;
            return;
        }

        e.Cancel = true;
        if (e.IsUserInitiated)
        {
            _player.TryOpenExternally(target);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Cancelled navigations also complete; only a failed load of the player page itself matters.
        if (!e.IsSuccess && e.NavigationId == _playerNavigationId)
        {
            _player.Fail(YouTubePlayerFailure.StartFailed);
        }
    }

    /// <summary>Never opens a browser window. A user-clicked YouTube link (title, logo) opens in the default browser.</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            _player.TryOpenExternally(target);
        }
    }

    /// <summary>Accepts plain string status messages from the packaged page only. Messages never carry URLs.</summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !YouTubePlayerPolicy.IsPlayerPage(source))
        {
            return;
        }

        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }

        _player.HandlePlayerMessage(message);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        e.Handled = true;
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) =>
        e.State = CoreWebView2PermissionState.Deny;

    /// <summary>GPU, utility, and other helper processes restart on their own; only a lost browser or renderer ends playback.</summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
        {
            _player.Fail(YouTubePlayerFailure.StartFailed);
        }
    }

    private void OnReadyTimeout(object? sender, EventArgs e)
    {
        _readyTimer.Stop();
        _player.ReportReadyTimeout();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VideoPlayerViewModel.State) || _player.IsStarting)
        {
            return;
        }

        _readyTimer.Stop();
        if (_player.IsUnavailable)
        {
            // Stop any audio and release the browser processes; the fallback replaces the player. Failures are
            // reported from WebView2 callbacks, so dispose after the callback returns.
            SetFullScreen(false);
            Dispatcher.BeginInvoke(DisposeBrowser);
        }
    }

    /// <summary>YouTube's full-screen button fills the main window with the video instead of the small player area.</summary>
    private void OnFullScreenChanged(object? sender, object e) => SetFullScreen(_core?.ContainsFullScreenElement == true);

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == _fullWindow)
        {
            return;
        }

        _fullWindow = fullScreen;
        if (fullScreen)
        {
            _restoreMargin = RootPanel.Margin;
            HeaderPanel.Visibility = Visibility.Collapsed;
            FooterPanel.Visibility = Visibility.Collapsed;
            RootPanel.Margin = default;
        }
        else
        {
            HeaderPanel.Visibility = Visibility.Visible;
            FooterPanel.Visibility = Visibility.Visible;
            RootPanel.Margin = _restoreMargin;
        }

        FullWindowChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeBrowser()
    {
        if (_browserDisposed)
        {
            return;
        }

        _browserDisposed = true;
        if (_core is { } core)
        {
            _core = null;
            try
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.WebMessageReceived -= OnWebMessageReceived;
                core.DownloadStarting -= OnDownloadStarting;
                core.PermissionRequested -= OnPermissionRequested;
                core.ProcessFailed -= OnProcessFailed;
                core.ContainsFullScreenElementChanged -= OnFullScreenChanged;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // The browser already closed (for example after its process exited); disposal below still runs.
            }
        }

        PlayerView.Dispose();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Stops playback and releases the browser before the layer leaves the window.</summary>
    public void OnOverlayClosed()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _readyTimer.Stop();
        _readyTimer.Tick -= OnReadyTimeout;
        _player.PropertyChanged -= OnPlayerChanged;
        DisposeBrowser();
    }
}
