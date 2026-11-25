using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>Lifecycle of the in-app player window.</summary>
public enum VideoPlayerState
{
    /// <summary>The embedded browser is starting or YouTube's player is loading.</summary>
    Starting,

    /// <summary>YouTube's player reported ready or started playing.</summary>
    Playing,

    /// <summary>The video cannot play in the app. The window offers the external link instead.</summary>
    Unavailable,
}

/// <summary>
/// State for the sandboxed in-app YouTube player. The view owns WebView2 mechanics and reports what happened;
/// this view model decides what the user sees. Once unavailable, the player never returns to playing, and every
/// failure leaves the external "Watch on YouTube" link available.
/// </summary>
public sealed partial class VideoPlayerViewModel : ObservableObject
{
    private readonly IDialogService? _dialogs;

    public VideoPlayerViewModel(string badgeText, string title, Uri videoUrl, YouTubeVideoId videoId, IDialogService? dialogs)
    {
        ArgumentNullException.ThrowIfNull(videoUrl);
        ArgumentNullException.ThrowIfNull(videoId);
        BadgeText = badgeText;
        Title = title;
        VideoUrl = videoUrl;
        VideoId = videoId;
        PlayerPageUrl = YouTubePlayerPolicy.PlayerPageUrl(videoId);
        _dialogs = dialogs;
    }

    public string BadgeText { get; }

    public string Title { get; }

    public string WindowTitle => $"{BadgeText}: {Title}";

    /// <summary>The original LiveChart-listed link, used for the external fallback.</summary>
    public Uri VideoUrl { get; }

    public YouTubeVideoId VideoId { get; }

    /// <summary>The packaged player page for this video on the virtual host.</summary>
    public Uri PlayerPageUrl { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsPlayerVisible), nameof(IsUnavailable), nameof(StatusText))]
    private VideoPlayerState _state = VideoPlayerState.Starting;

    /// <summary>Why the player is unavailable, or null while starting or playing.</summary>
    [ObservableProperty]
    private YouTubePlayerFailure? _failure;

    [ObservableProperty]
    private string _unavailableMessage = string.Empty;

    /// <summary>True once the video has actually started playing, not just loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _hasStartedPlayback;

    public bool IsStarting => State == VideoPlayerState.Starting;

    public bool IsPlayerVisible => State != VideoPlayerState.Unavailable;

    public bool IsUnavailable => State == VideoPlayerState.Unavailable;

    public string StatusText => State switch
    {
        VideoPlayerState.Starting => "Loading the YouTube player…",
        VideoPlayerState.Playing => HasStartedPlayback ? "Playing from YouTube" : "Ready. Press play in the player",
        _ => "Can't play in the app",
    };

    /// <summary>Handles a raw message from the player page. Unrecognized messages are ignored.</summary>
    public void HandlePlayerMessage(string? message)
    {
        switch (YouTubePlayerPolicy.ParseMessage(message))
        {
            case YouTubePlayerMessage.Ready when State == VideoPlayerState.Starting:
                State = VideoPlayerState.Playing;
                break;
            case YouTubePlayerMessage.Started when State != VideoPlayerState.Unavailable:
                HasStartedPlayback = true;
                State = VideoPlayerState.Playing;
                break;
            case YouTubePlayerMessage.Failed error:
                Fail(YouTubePlayerPolicy.ClassifyError(error.Code));
                break;
            case YouTubePlayerMessage.Unreachable:
                Fail(YouTubePlayerFailure.Unreachable);
                break;
        }
    }

    /// <summary>The player did not report ready within <see cref="YouTubePlayerPolicy.ReadyTimeout"/>.</summary>
    public void ReportReadyTimeout()
    {
        if (State == VideoPlayerState.Starting)
        {
            Fail(YouTubePlayerFailure.Unreachable);
        }
    }

    /// <summary>Switches to the unavailable state. Later failures do not replace the first reason.</summary>
    public void Fail(YouTubePlayerFailure failure)
    {
        if (State == VideoPlayerState.Unavailable)
        {
            return;
        }

        Failure = failure;
        UnavailableMessage = Describe(failure);
        State = VideoPlayerState.Unavailable;
    }

    /// <summary>
    /// Opens a link the user clicked inside the player (such as the video title) in the default browser, but only
    /// when it is an allowed video link. Returns false when the link was dropped.
    /// </summary>
    public bool TryOpenExternally(Uri? url)
    {
        if (!ExternalUrlPolicy.IsAllowedVideoUrl(url))
        {
            return false;
        }

        _dialogs?.OpenExternalUrl(url!);
        return true;
    }

    [RelayCommand]
    private void WatchOnYouTube() => _dialogs?.OpenExternalUrl(VideoUrl);

    [RelayCommand]
    private void OpenTerms() => _dialogs?.OpenExternalUrl(YouTubePlayerPolicy.TermsOfServiceUrl);

    /// <summary>User-facing explanation for each failure. Every message points to the external link.</summary>
    public static string Describe(YouTubePlayerFailure failure) => failure switch
    {
        YouTubePlayerFailure.EmbeddingDisabled =>
            "The uploader doesn't allow this video to play outside YouTube. Use Watch on YouTube to open it in your browser.",
        YouTubePlayerFailure.NotFound =>
            "YouTube says this video was removed or made private.",
        YouTubePlayerFailure.IdentificationRejected =>
            "YouTube refused to play this video in the app. Use Watch on YouTube to open it in your browser.",
        YouTubePlayerFailure.Unreachable =>
            "YouTube could not be reached. Check your internet connection, or use Watch on YouTube to try in your browser.",
        YouTubePlayerFailure.RuntimeMissing =>
            "Playing videos in the app needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11. Use Watch on YouTube to open the video in your browser.",
        YouTubePlayerFailure.StartFailed =>
            "The in-app player could not start. Use Watch on YouTube to open the video in your browser.",
        _ =>
            "YouTube could not play this video in the app. Use Watch on YouTube to open it in your browser.",
    };
}
