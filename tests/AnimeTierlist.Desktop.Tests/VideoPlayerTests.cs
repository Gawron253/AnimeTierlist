using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Desktop.Views;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>In-app YouTube player: fallback behavior, link routing, and an opt-in live playback check.</summary>
public sealed class VideoPlayerTests
{
    private static readonly Uri SampleUrl = new("https://www.youtube.com/watch?v=0gU8da5iHAA");

    [Fact]
    public void Ready_shows_the_player_and_a_later_embed_error_switches_to_the_fallback()
    {
        var dialogs = new FakeDialogs();
        var player = CreatePlayer(dialogs);
        Assert.True(player.IsStarting);
        Assert.True(player.IsPlayerVisible);

        player.HandlePlayerMessage("ready");
        Assert.Equal(VideoPlayerState.Playing, player.State);

        // YouTube can report "embedding disabled" after the player is ready, when playback starts.
        player.HandlePlayerMessage("error:150");
        Assert.True(player.IsUnavailable);
        Assert.False(player.IsPlayerVisible);
        Assert.Equal(YouTubePlayerFailure.EmbeddingDisabled, player.Failure);
        Assert.Contains("Watch on YouTube", player.UnavailableMessage, StringComparison.Ordinal);

        // A late "ready" or a second error never brings the player back or replaces the first reason.
        player.HandlePlayerMessage("ready");
        player.HandlePlayerMessage("error:153");
        Assert.True(player.IsUnavailable);
        Assert.Equal(YouTubePlayerFailure.EmbeddingDisabled, player.Failure);

        player.WatchOnYouTubeCommand.Execute(null);
        Assert.Equal([SampleUrl], dialogs.OpenedUrls);
    }

    [Theory]
    [InlineData("unreachable", YouTubePlayerFailure.Unreachable)]
    [InlineData("error:100", YouTubePlayerFailure.NotFound)]
    [InlineData("error:153", YouTubePlayerFailure.IdentificationRejected)]
    [InlineData("error:5", YouTubePlayerFailure.PlaybackError)]
    public void Player_failures_before_ready_show_the_fallback(string message, YouTubePlayerFailure expected)
    {
        var player = CreatePlayer(new FakeDialogs());
        player.HandlePlayerMessage(message);
        Assert.True(player.IsUnavailable);
        Assert.Equal(expected, player.Failure);
        Assert.False(string.IsNullOrWhiteSpace(player.UnavailableMessage));
    }

    [Fact]
    public void Unknown_messages_are_ignored_and_a_timeout_only_applies_while_starting()
    {
        var player = CreatePlayer(new FakeDialogs());
        player.HandlePlayerMessage("navigate:https://evil.example");
        player.HandlePlayerMessage(null);
        Assert.True(player.IsStarting);

        player.ReportReadyTimeout();
        Assert.Equal(YouTubePlayerFailure.Unreachable, player.Failure);

        var playing = CreatePlayer(new FakeDialogs());
        playing.HandlePlayerMessage("ready");
        playing.ReportReadyTimeout();
        Assert.Equal(VideoPlayerState.Playing, playing.State);
    }

    [Fact]
    public void Links_clicked_inside_the_player_open_externally_only_when_allowed()
    {
        var dialogs = new FakeDialogs();
        var player = CreatePlayer(dialogs);

        Assert.True(player.TryOpenExternally(new Uri("https://www.youtube.com/watch?v=hvcWx-8RfDQ")));
        Assert.False(player.TryOpenExternally(new Uri("https://ads.example/landing")));
        Assert.False(player.TryOpenExternally(new Uri("http://www.youtube.com/watch?v=hvcWx-8RfDQ")));
        Assert.False(player.TryOpenExternally(null));

        player.OpenTermsCommand.Execute(null);
        Assert.Equal([new Uri("https://www.youtube.com/watch?v=hvcWx-8RfDQ"), YouTubePlayerPolicy.TermsOfServiceUrl], dialogs.OpenedUrls);
        Assert.All(dialogs.OpenedUrls, url => Assert.True(ExternalUrlPolicy.IsAllowedBrowserLaunchUrl(url)));
    }

    [Fact]
    public void Video_rows_play_youtube_links_in_the_app_and_open_other_links_externally()
    {
        var youTube = new VideoLinkViewModel(new AnimeVideo("OP", SampleUrl, AnimeVideoKind.Opening, "1:30"));
        Assert.True(youTube.CanPlayInApp);
        Assert.Equal("0gU8da5iHAA", youTube.YouTubeId!.Value);
        Assert.Equal("OP: OP (1:30), plays in the app", youTube.AccessibleName);
        Assert.Equal("OP: OP (1:30), open in external browser", youTube.OpenExternallyAccessibleName);

        var x = new VideoLinkViewModel(new AnimeVideo("Teaser", new Uri("https://x.com/studio/status/1"), AnimeVideoKind.Other));
        Assert.False(x.CanPlayInApp);
        Assert.Equal("Video: Teaser, opens in external browser", x.AccessibleName);
    }

    /// <summary>
    /// Opt-in live check of the real player window against YouTube. Set <c>ANIMETIERLIST_LIVE_PLAYER_CHECK</c> to a
    /// comma-separated list of YouTube links. Each video must start playing or reach a reviewed fallback reason
    /// (embedding disabled, removed); identification, start, and reachability failures fail the check. Set
    /// <c>ANIMETIERLIST_LIVE_PLAYER_SCREENSHOTS</c> to a folder to save a capture of the player window only.
    /// </summary>
    [Fact]
    public async Task Live_player_plays_or_falls_back_when_requested()
    {
        var setting = Environment.GetEnvironmentVariable("ANIMETIERLIST_LIVE_PLAYER_CHECK");
        if (string.IsNullOrWhiteSpace(setting))
        {
            Assert.Skip("Set ANIMETIERLIST_LIVE_PLAYER_CHECK=<comma-separated YouTube links> to check the in-app player live.");
            return;
        }

        if (!VideoPlayerEnvironment.IsRuntimeAvailable())
        {
            Assert.Fail("The WebView2 Runtime is not installed on this machine.");
        }

        var screenshots = Environment.GetEnvironmentVariable("ANIMETIERLIST_LIVE_PLAYER_SCREENSHOTS");
        var dataRoot = Path.Combine(Path.GetTempPath(), $"animetierlist-player-{Guid.NewGuid():N}");
        var environment = new VideoPlayerEnvironment(dataRoot, AppContext.BaseDirectory);
        var results = new List<string>();
        try
        {
            await Ui.RunAsync(async () =>
            {
                foreach (var link in setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var url = new Uri(link);
                    Assert.True(YouTubeVideoId.TryParse(url, out var id), $"{link} is not a playable YouTube link.");
                    var player = new VideoPlayerViewModel("OP", link, url, id, dialogs: null);
                    // Topmost keeps the window unoccluded, so the browser keeps rendering and autoplay is not deferred.
                    var window = new VideoPlayerWindow(player, environment) { ShowActivated = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                    try
                    {
                        window.Show();
                        var deadline = DateTime.UtcNow + YouTubePlayerPolicy.ReadyTimeout + TimeSpan.FromSeconds(5);
                        while (player.IsStarting && DateTime.UtcNow < deadline)
                        {
                            await Task.Delay(250);
                        }

                        // Embedding blocks can arrive after "ready", once playback starts.
                        if (player.State == VideoPlayerState.Playing)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(6));
                        }

                        if (!string.IsNullOrWhiteSpace(screenshots))
                        {
                            Directory.CreateDirectory(screenshots);
                            CaptureWindow(window, Path.Combine(screenshots, $"player-{id}.png"));
                        }

                        var outcome = player.State == VideoPlayerState.Playing
                            ? (player.HasStartedPlayback ? "Started" : "ReadyNotStarted")
                            : $"{player.State} {player.Failure}";
                        results.Add($"{id}: {outcome}");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            });
        }
        finally
        {
            // Best effort: browser processes may still hold profile files for a moment after the last window closes.
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        TestContext.Current.SendDiagnosticMessage("In-app player results:\n" + string.Join('\n', results));
        foreach (var line in results)
        {
            Assert.True(
                line.EndsWith(": Started", StringComparison.Ordinal)
                || line.Contains(nameof(YouTubePlayerFailure.EmbeddingDisabled), StringComparison.Ordinal)
                || line.Contains(nameof(YouTubePlayerFailure.NotFound), StringComparison.Ordinal),
                "Unexpected player outcome: " + string.Join("; ", results));
        }
    }

    /// <summary>Captures only the given window, including its WebView2 content, to a PNG file.</summary>
    private static void CaptureWindow(Window window, string path)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Assert.True(NativeMethods.GetWindowRect(hwnd, out var rect));
        var (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        var screen = NativeMethods.GetDC(IntPtr.Zero);
        var memory = NativeMethods.CreateCompatibleDC(screen);
        var bitmap = NativeMethods.CreateCompatibleBitmap(screen, width, height);
        var previous = NativeMethods.SelectObject(memory, bitmap);
        try
        {
            Assert.True(NativeMethods.PrintWindow(hwnd, memory, NativeMethods.RenderFullContent));
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        finally
        {
            NativeMethods.SelectObject(memory, previous);
            NativeMethods.DeleteObject(bitmap);
            NativeMethods.DeleteDC(memory);
            _ = NativeMethods.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static VideoPlayerViewModel CreatePlayer(IDialogService dialogs)
    {
        Assert.True(YouTubeVideoId.TryParse(SampleUrl, out var id));
        return new VideoPlayerViewModel("OP", "OP", SampleUrl, id, dialogs);
    }

    private static class NativeMethods
    {
        public const uint RenderFullContent = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
