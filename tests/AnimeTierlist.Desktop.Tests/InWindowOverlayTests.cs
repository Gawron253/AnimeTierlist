using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Desktop.Views;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Anime details and the video player open as layers inside the main window, so sharing that one window (for
/// example in Discord) shows them. These tests cover layer stacking, input blocking behind a layer, Esc, full-window
/// video, and the real details and player views opened through the dialog service.
/// </summary>
public sealed class InWindowOverlayTests
{
    [Fact]
    public Task Layers_stack_inside_the_main_window_and_disable_the_workspace_until_the_last_closes() => Ui.RunAsync(async () =>
    {
        var window = new MainWindow();
        var overlays = window.Overlays;
        Assert.False(overlays.IsOpen);
        Assert.Equal(Visibility.Collapsed, overlays.Visibility);
        Assert.True(window.WorkspaceRoot.IsEnabled);

        var details = new FakeOverlayView();
        overlays.Show(details);
        Assert.True(overlays.IsOpen);
        Assert.Equal(Visibility.Visible, overlays.Visibility);
        Assert.False(window.WorkspaceRoot.IsEnabled);
        Assert.Same(window, Window.GetWindow(details));

        // The player opens above the details, which stay in the window, hidden, with their state.
        var player = new FakeOverlayView();
        overlays.Show(player);
        Assert.Same(player, overlays.Top);
        Assert.Equal(Visibility.Collapsed, CardOf(details).Visibility);
        Assert.Equal(Visibility.Visible, CardOf(player).Visibility);

        // Esc closes only the top layer, after the key event returns.
        Assert.True(overlays.HandleEscape());
        Assert.Equal(0, player.ClosedCount);
        await DispatcherIdleAsync();
        Assert.Equal(1, player.ClosedCount);
        Assert.Null(VisualTreeHelper.GetParent(player));
        Assert.Same(details, overlays.Top);
        Assert.Equal(Visibility.Visible, CardOf(details).Visibility);
        Assert.False(window.WorkspaceRoot.IsEnabled);

        // The view's own Close button (or view-model request) closes the last layer and re-enables the workspace.
        details.RequestClose();
        Assert.Equal(1, details.ClosedCount);
        Assert.False(overlays.IsOpen);
        Assert.Equal(Visibility.Collapsed, overlays.Visibility);
        Assert.True(window.WorkspaceRoot.IsEnabled);

        // A late request from a closed view, and Esc with nothing open, change nothing.
        details.RequestClose();
        Assert.Equal(1, details.ClosedCount);
        Assert.False(overlays.HandleEscape());
    });

    [Fact]
    public Task A_queued_escape_does_not_close_the_layer_revealed_after_its_target_already_closed() => Ui.RunAsync(async () =>
    {
        var window = new MainWindow();
        var overlays = window.Overlays;
        var details = new FakeOverlayView();
        var player = new FakeOverlayView();
        overlays.Show(details);
        overlays.Show(player);

        // Esc is queued for the player, which then closes another way (its Close button) before the queue runs.
        Assert.True(overlays.HandleEscape());
        player.RequestClose();
        await DispatcherIdleAsync();
        Assert.Equal(1, player.ClosedCount);
        Assert.Equal(0, details.ClosedCount);
        Assert.Same(details, overlays.Top);
    });

    [Fact]
    public Task Closing_a_lower_layer_closes_the_layers_above_it_and_close_all_releases_every_view() => Ui.RunAsync(() =>
    {
        var window = new MainWindow();
        var overlays = window.Overlays;
        var first = new FakeOverlayView();
        var second = new FakeOverlayView();
        overlays.Show(first);
        overlays.Show(second);

        overlays.Close(first);
        Assert.Equal(1, first.ClosedCount);
        Assert.Equal(1, second.ClosedCount);
        Assert.False(overlays.IsOpen);

        var third = new FakeOverlayView();
        var fourth = new FakeOverlayView();
        overlays.Show(third);
        overlays.Show(fourth);
        overlays.CloseAll();
        Assert.Equal(1, third.ClosedCount);
        Assert.Equal(1, fourth.ClosedCount);
        Assert.False(overlays.IsOpen);
        Assert.True(window.WorkspaceRoot.IsEnabled);
        Assert.Single(overlays.Children);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_full_window_video_fills_the_window_and_ignores_escape_until_it_leaves_full_screen() => Ui.RunAsync(async () =>
    {
        var window = new MainWindow();
        var overlays = window.Overlays;
        var player = new FakeOverlayView { FillsHeight = true };
        overlays.Show(player);
        var card = CardOf(player);
        Assert.Equal(new Thickness(24), card.Margin);
        Assert.Equal(1040, card.MaxWidth);
        Assert.Equal(VerticalAlignment.Stretch, card.VerticalAlignment);

        player.SetFullWindow(true);
        Assert.Equal(default, card.Margin);
        Assert.True(double.IsPositiveInfinity(card.MaxWidth));
        Assert.True(double.IsPositiveInfinity(card.MaxHeight));
        Assert.True(overlays.HandleEscape());
        await DispatcherIdleAsync();
        Assert.Equal(0, player.ClosedCount);

        player.SetFullWindow(false);
        Assert.Equal(new Thickness(24), card.Margin);
        Assert.Equal(1040, card.MaxWidth);
        Assert.True(overlays.HandleEscape());
        await DispatcherIdleAsync();
        Assert.Equal(1, player.ClosedCount);
    });

    [Fact]
    public Task Window_shortcuts_are_swallowed_while_a_layer_is_open_and_escape_closes_it() => Ui.RunAsync(async () =>
    {
        var window = new MainWindow();
        var source = new DummyPresentationSource(window);

        // No layer: F1 reaches the window's key binding as usual.
        Assert.False(PressPreview(window, source, Key.F1));

        var layer = new FakeOverlayView();
        window.Overlays.Show(layer);

        // F1 (shortcuts help) is a window binding; it must not open behind the layer.
        Assert.True(PressPreview(window, source, Key.F1));
        Assert.True(window.Overlays.IsOpen);

        // Keys the window does not bind pass through to the layer.
        Assert.False(PressPreview(window, source, Key.Tab));

        // Esc closes after the key event returns: WebView2 forwards it from a callback where the browser must not be disposed.
        Assert.True(PressPreview(window, source, Key.Escape));
        Assert.Equal(0, layer.ClosedCount);
        await DispatcherIdleAsync();
        Assert.Equal(1, layer.ClosedCount);
        Assert.False(window.Overlays.IsOpen);
    });

    [Fact]
    public Task Details_and_player_open_as_layers_in_the_main_window_through_the_dialog_service() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        collection.Entries[0].Included = true;
        collection.Entries[1].Included = true;
        collection.NewBoardName = "Stream";
        await collection.CreateBoardCommand.ExecuteAsync(null);
        var board = Assert.IsType<BoardViewModel>(harness.Project.Content);

        var window = new MainWindow();
        var previousDialogs = harness.App.Dialogs;
        harness.App.Dialogs = new WpfDialogService(window, window.Overlays);
        try
        {
            board.OpenEntryDetailsCommand.Execute(board.Unranked.Entries[0]);
            var detailsView = Assert.IsType<BoardEntryDetailsView>(window.Overlays.Top);
            Assert.Same(window, Window.GetWindow(detailsView));
            Assert.False(window.WorkspaceRoot.IsEnabled);
            var details = Assert.IsType<BoardEntryDetailsViewModel>(detailsView.DataContext);

            // Playing a YouTube link opens the player above the details, in the same window.
            var video = new VideoLinkViewModel(new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=0gU8da5iHAA"), AnimeVideoKind.Opening, "1:30"));
            details.PlayVideoCommand.Execute(video);
            var playerView = Assert.IsType<VideoPlayerView>(window.Overlays.Top);
            Assert.Same(window, Window.GetWindow(playerView));
            Assert.Equal(Visibility.Collapsed, CardOf(detailsView).Visibility);

            // Closing the player returns to the same details view.
            window.Overlays.Close(playerView);
            Assert.Same(detailsView, window.Overlays.Top);
            Assert.Equal(Visibility.Visible, CardOf(detailsView).Visibility);
            window.Overlays.Close(detailsView);
            Assert.False(window.Overlays.IsOpen);
            Assert.True(window.WorkspaceRoot.IsEnabled);

            // Removing a duplicate from its details closes the layer, as the view model requests.
            var duplicate = Assert.IsType<BoardEntry>(board.DuplicateEntry(board.Unranked.Entries[0].Item));
            var duplicateCard = board.Unranked.Entries.Single(e => e.Key == duplicate.Key);
            board.OpenEntryDetailsCommand.Execute(duplicateCard);
            var duplicateDetails = Assert.IsType<BoardEntryDetailsViewModel>(Assert.IsType<BoardEntryDetailsView>(window.Overlays.Top).DataContext);
            duplicateDetails.RemoveThisCopyCommand.Execute(null);
            Assert.False(window.Overlays.IsOpen);
        }
        finally
        {
            window.Overlays.CloseAll();
            harness.App.Dialogs = previousDialogs;
        }
    });

    private static Border CardOf(FrameworkElement view) => Assert.IsType<Border>(view.Parent);

    /// <summary>Runs queued input-priority work, such as a deferred Esc close.</summary>
    private static Task DispatcherIdleAsync() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background).Task;

    private static bool PressPreview(UIElement target, PresentationSource source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }

    private sealed class FakeOverlayView : UserControl, IOverlayView
    {
        private bool _fullWindow;

        public FakeOverlayView() => Content = new Button { Content = "Close" };

        public event EventHandler? CloseRequested;

        public event EventHandler? FullWindowChanged;

        public int ClosedCount { get; private set; }

        public bool IsFullWindow => _fullWindow;

        public Size MaxCardSize => new(1040, 720);

        public bool FillsHeight { get; init; }

        public void OnOverlayClosed() => ClosedCount++;

        public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

        public void SetFullWindow(bool value)
        {
            _fullWindow = value;
            FullWindowChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class DummyPresentationSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get => root; set { } }

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
