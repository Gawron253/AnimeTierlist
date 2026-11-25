using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Infrastructure.LiveChart;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>Display item for a single external video link (OP, ED, or other video) in the board details dialog.</summary>
public sealed class VideoLinkViewModel
{
    public VideoLinkViewModel(AnimeVideo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        Title = video.Title;
        Url = video.Url;
        Kind = video.Kind;
        Duration = video.Duration;
        HasSpoilers = video.HasSpoilers;

        BadgeText = video.Kind switch
        {
            AnimeVideoKind.Opening => "OP",
            AnimeVideoKind.Ending => "ED",
            _ => "Video",
        };

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(video.AudioLanguage))
        {
            parts.Add($"Audio: {video.AudioLanguage}");
        }

        if (!string.IsNullOrWhiteSpace(video.Subtitles))
        {
            parts.Add($"Sub: {video.Subtitles}");
        }

        LanguageSummary = parts.Count > 0 ? string.Join(" · ", parts) : null;
        YouTubeId = YouTubeVideoId.TryParse(video.Url, out var youTubeId) ? youTubeId : null;
        var label = Duration is { Length: > 0 } ? $"{BadgeText}: {Title} ({Duration})" : $"{BadgeText}: {Title}";
        AccessibleName = CanPlayInApp ? $"{label}, plays in the app" : $"{label}, opens in external browser";
        OpenExternallyAccessibleName = $"{label}, open in external browser";
    }

    public string Title { get; }

    public Uri Url { get; }

    public AnimeVideoKind Kind { get; }

    public string BadgeText { get; }

    public bool IsOpening => Kind == AnimeVideoKind.Opening;

    public bool IsEnding => Kind == AnimeVideoKind.Ending;

    public bool IsThemeSong => AnimeVideoClassifier.IsThemeSong(Kind);

    public string? Duration { get; }

    public string? LanguageSummary { get; }

    public bool HasSpoilers { get; }

    /// <summary>The validated YouTube video ID, or null for links the in-app player cannot play (X, LiveChart).</summary>
    public YouTubeVideoId? YouTubeId { get; }

    /// <summary>True for YouTube links, which play in the app's sandboxed player.</summary>
    public bool CanPlayInApp => YouTubeId is not null;

    /// <summary>Screen-reader name for the main row action: play in the app, or open externally.</summary>
    public string AccessibleName { get; }

    /// <summary>Screen-reader name for the row's secondary "open in external browser" button.</summary>
    public string OpenExternallyAccessibleName { get; }
}

/// <summary>
/// View model for the board card double-click dialog: displays captured and on-demand LiveChart details,
/// OP/ED and other videos (in-app YouTube player or external browser), AniList/MyAnimeList/LiveChart links, and entry
/// duplication controls.
/// </summary>
public sealed partial class BoardEntryDetailsViewModel : ObservableObject, IDisposable
{
    public const string ThemesTab = "Themes";
    public const string OtherTab = "Other";

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly BoardViewModel _board;
    private readonly BoardEntryViewModel _entry;
    private readonly SourceDetails _snapshotDetails;
    private SourceDetails _cachedProfileDetails = SourceDetails.Empty;
    private ImmutableArray<AnimeVideo> _cachedVideos = [];
    private bool _hasLoadedVideos;
    private bool _hasLoadedProfile;
    private bool _lastVideosFailed;
    private bool _lastProfileFailed;
    private AnimeExtraDetails? _expectedCommittedExtras;
    private string _rejectedSaveError = string.Empty;
    private int _syncQueued;
    private bool _disposed;
    private CancellationTokenSource? _fetchCts;

    [ObservableProperty]
    private string _copySummary = string.Empty;

    [ObservableProperty]
    private string _duplicateFeedback = string.Empty;

    [ObservableProperty]
    private int _totalCopiesCount;

    [ObservableProperty]
    private string? _rating;

    [ObservableProperty]
    private string? _premiere;

    [ObservableProperty]
    private string? _metadata;

    [ObservableProperty]
    private string? _synopsis;

    [ObservableProperty]
    private string? _studiosText;

    [ObservableProperty]
    private IReadOnlyList<string> _tags = [];

    [ObservableProperty]
    private bool _hasExtendedDetails;

    [ObservableProperty]
    private bool _showOlderSnapshotHint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAniListUrl))]
    [NotifyCanExecuteChangedFor(nameof(OpenOnAniListCommand))]
    private Uri? _aniListUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMyAnimeListUrl))]
    [NotifyCanExecuteChangedFor(nameof(OpenOnMyAnimeListCommand))]
    private Uri? _myAnimeListUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowThemeVideos), nameof(ShowOtherVideos))]
    private string _selectedVideoTab = ThemesTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoThemeVideos), nameof(HasNoOtherVideos))]
    [NotifyCanExecuteChangedFor(nameof(RefreshLiveDataCommand), nameof(RetryLiveDataCommand), nameof(RetrySaveCommand))]
    private bool _isLoadingLiveData;

    [ObservableProperty]
    private string _liveDataStatus = string.Empty;

    [ObservableProperty]
    private string _liveDataError = string.Empty;

    [ObservableProperty]
    private string _saveError = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetrySaveCommand))]
    private bool _isRetryingSave;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetrySaveCommand))]
    private bool _showRetrySaveButton;

    [ObservableProperty]
    private bool _hasCachedLiveData;

    /// <summary>Creates a LiveChart details, videos, external links, and duplication view model for a board entry.</summary>
    public BoardEntryDetailsViewModel(BoardViewModel board, BoardEntryViewModel entry, ProjectState state)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(state);

        _board = board;
        _entry = entry;

        Title = entry.Title;
        Item = entry.Item;
        Key = entry.Key;
        LiveChartUrl = LiveChartRoutes.AnimePage(entry.Item);
        LiveChartVideosUrl = LiveChartRoutes.AnimeVideosPage(entry.Item);
        LiveChartIdText = $"LiveChart #{entry.Item.Value.ToString(CultureInfo.InvariantCulture)}";

        SourceOccurrence? observation = null;
        var appearances = new List<string>();
        if (state.Collections.TryGetValue(board.Collection, out var collection))
        {
            SourceCollectionName = collection.Name;
            if (collection.IndexOf(entry.Item) >= 0)
            {
                var collectionEntry = collection.Entry(entry.Item);
                observation = state.Observation(collectionEntry.AcceptedOccurrence);
                if (state.Snapshots.TryGetValue(collection.AppliedSnapshot, out var snapshot))
                {
                    foreach (var capture in snapshot.Captures)
                    {
                        foreach (var occ in capture.Occurrences)
                        {
                            if (occ.Item == entry.Item)
                            {
                                appearances.Add($"{capture.Season} · {ProjectViewModel.CategoryLabel(capture.Category)} (Popularity #{occ.Ordinal + 1})");
                            }
                        }
                    }
                }
            }
        }

        if (observation is not null)
        {
            DisplayedTitle = observation.Titles.Displayed;
            EnglishTitle = observation.Titles.English;
            RomanizedTitle = observation.Titles.Romanized;
            NativeTitle = observation.Titles.Native;
            _snapshotDetails = observation.Details;
        }
        else
        {
            DisplayedTitle = entry.Title;
            _snapshotDetails = SourceDetails.Empty;
        }

        SourceAppearanceText = appearances.Count > 0
            ? string.Join("  ·  ", appearances)
            : SourceCollectionName is { Length: > 0 } ? $"From collection '{SourceCollectionName}'" : "Captured in project";

        if (_board.Project.TryGetPendingItemExtras(entry.Item) is { } staged)
        {
            _cachedProfileDetails = staged.Details;
            _cachedVideos = staged.Videos;
            _hasLoadedVideos = staged.VideosFetched;
            _hasLoadedProfile = staged.ProfileFetched;
            _expectedCommittedExtras = staged;
            ApplyDetails(_cachedProfileDetails.MergeWith(_snapshotDetails));
            ApplyVideos(_cachedVideos, autoSelectOtherIfNoThemes: _hasLoadedVideos);
        }
        else if (state.ItemExtras.TryGetValue(entry.Item, out var cached))
        {
            _cachedProfileDetails = cached.Details;
            _cachedVideos = cached.Videos;
            _hasLoadedVideos = cached.VideosFetched;
            _hasLoadedProfile = cached.ProfileFetched;
            if (!(_board.Project.Session.Durable.ItemExtras.TryGetValue(entry.Item, out var durable) && durable == cached))
            {
                _expectedCommittedExtras = cached;
            }

            ApplyDetails(_cachedProfileDetails.MergeWith(_snapshotDetails));
            ApplyVideos(_cachedVideos, autoSelectOtherIfNoThemes: _hasLoadedVideos);
        }
        else
        {
            ApplyDetails(_snapshotDetails);
            ApplyVideos([], autoSelectOtherIfNoThemes: false);
        }

        RefreshCopies(state);
        _board.Project.Session.Changed += OnSessionChanged;
        SyncSaveState();
    }

    /// <summary>Raised when the dialog should close (for example after removing the inspected duplicate copy).</summary>
    public event EventHandler? RequestClose;

    public BoardEntryKey Key { get; }

    public LiveChartId Item { get; }

    public int CopyNumber => Key.CopyNumber;

    public bool IsDuplicate => Key.IsDuplicate;

    public string Title { get; }

    public string WindowTitle => IsDuplicate ? $"{Title} (Copy #{CopyNumber})" : Title;

    public string LiveChartIdText { get; }

    public Uri LiveChartUrl { get; }

    public Uri LiveChartVideosUrl { get; }

    public bool HasAniListUrl => AniListUrl is not null;

    public bool HasMyAnimeListUrl => MyAnimeListUrl is not null;

    public string? SourceCollectionName { get; }

    public string SourceAppearanceText { get; }

    public string DisplayedTitle { get; }

    public string? EnglishTitle { get; }

    public string? RomanizedTitle { get; }

    public string? NativeTitle { get; }

    public bool HasEnglishTitle => !string.IsNullOrWhiteSpace(EnglishTitle) && !string.Equals(EnglishTitle, DisplayedTitle, StringComparison.Ordinal);

    public bool HasRomanizedTitle => !string.IsNullOrWhiteSpace(RomanizedTitle) && !string.Equals(RomanizedTitle, DisplayedTitle, StringComparison.Ordinal);

    public bool HasNativeTitle => !string.IsNullOrWhiteSpace(NativeTitle);

    public bool EditsEnabled => !_board.Project.Session.IsSuspended;

    public ImageSource? Thumbnail => _entry.Thumbnail;

    public bool HasCover => _entry.HasCover;

    public bool IsBrokenCover => _entry.IsBrokenCover;

    public string CoverPlaceholder => _entry.CoverPlaceholder;

    public string RemoveCopyButtonLabel => $"Remove copy #{CopyNumber}";

    public ObservableCollection<string> Copies { get; } = [];

    /// <summary>Prioritized opening and ending theme video links for this anime.</summary>
    public ObservableCollection<VideoLinkViewModel> ThemeVideos { get; } = [];

    /// <summary>Non-theme promotional videos (PVs, trailers, spots, teasers, insert songs).</summary>
    public ObservableCollection<VideoLinkViewModel> OtherVideos { get; } = [];

    public string ThemeVideosTabLabel => $"OP / ED ({ThemeVideos.Count})";

    public string OtherVideosTabLabel => $"Other videos ({OtherVideos.Count})";

    public bool ShowThemeVideos => SelectedVideoTab == ThemesTab;

    public bool ShowOtherVideos => SelectedVideoTab == OtherTab;

    public bool HasNoThemeVideos => !IsLoadingLiveData && _hasLoadedVideos && !_lastVideosFailed && ThemeVideos.Count == 0;

    public bool HasNoOtherVideos => !IsLoadingLiveData && _hasLoadedVideos && !_lastVideosFailed && OtherVideos.Count == 0;

    /// <summary>
    /// Automatically fetches any missing on-demand videos or profile details from LiveChart on first open.
    /// Once both pages are cached inside the project database, reopening requires zero network requests.
    /// </summary>
    public Task EnsureLoadedAsync()
    {
        if ((_hasLoadedVideos && _hasLoadedProfile) || IsLoadingLiveData)
        {
            return Task.CompletedTask;
        }

        return FetchFromLiveChartAsync(fetchVideos: !_hasLoadedVideos, fetchProfile: !_hasLoadedProfile);
    }

    /// <summary>Cancels any in-flight on-demand LiveChart fetch and unsubscribes from session updates.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _board.Project.Session.Changed -= OnSessionChanged;
        CancelPendingLoads();
    }

    /// <summary>Cancels any in-flight on-demand LiveChart fetch when the dialog closes.</summary>
    public void CancelPendingLoads()
    {
        var cts = Interlocked.Exchange(ref _fetchCts, null);
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            cts.Dispose();
        }
    }

    /// <summary>Fetches fresh OP/ED videos, full details, and AniList/MyAnimeList links from LiveChart on demand.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchLiveData))]
    private Task RefreshLiveDataAsync() => FetchFromLiveChartAsync(fetchVideos: true, fetchProfile: true);

    /// <summary>Retries only the LiveChart page(s) that failed on the latest fetch.</summary>
    [RelayCommand(CanExecute = nameof(CanFetchLiveData))]
    private Task RetryLiveDataAsync()
    {
        var retryVideos = _lastVideosFailed || (!_lastProfileFailed && !_hasLoadedVideos);
        var retryProfile = _lastProfileFailed || (!_lastVideosFailed && !_hasLoadedProfile);
        if (!retryVideos && !retryProfile)
        {
            retryVideos = true;
            retryProfile = true;
        }

        return FetchFromLiveChartAsync(fetchVideos: retryVideos, fetchProfile: retryProfile);
    }

    /// <summary>Retries failed project saves and flushes staged on-demand LiveChart data without repeating HTTP requests.</summary>
    [RelayCommand(CanExecute = nameof(CanRetrySave))]
    private async Task RetrySaveAsync()
    {
        if (IsRetryingSave)
        {
            return;
        }

        IsRetryingSave = true;
        try
        {
            await _board.Project.RecoverAndFlushPendingItemExtrasAsync();
        }
        finally
        {
            IsRetryingSave = false;
            SyncSaveState();
        }
    }

    private bool CanFetchLiveData() => !IsLoadingLiveData;

    private bool CanRetrySave() => !IsRetryingSave && !IsLoadingLiveData && ShowRetrySaveButton;

    /// <summary>Creates a new numbered duplicate copy of this anime in the board's Unranked pool next to the original.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateEntry()
    {
        if (_board.DuplicateEntry(Item) is { } created)
        {
            RefreshCopies(_board.Project.Session.Current);
            DuplicateFeedback = $"Added copy #{created.CopyNumber} in Unranked next to the original.";
        }
    }

    private bool CanDuplicate() => EditsEnabled;

    /// <summary>Removes this duplicated copy from the board and closes the dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveThisCopy))]
    private void RemoveThisCopy()
    {
        if (IsDuplicate && _board.RemoveDuplicateEntry(Key))
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanRemoveThisCopy() => IsDuplicate && EditsEnabled;

    /// <summary>Opens the anime's public LiveChart page in the user's default browser.</summary>
    [RelayCommand]
    private void OpenOnLiveChart() => _board.Project.App.Dialogs?.OpenExternalUrl(LiveChartUrl);

    /// <summary>Opens the anime's AniList page in the user's default browser.</summary>
    [RelayCommand(CanExecute = nameof(HasAniListUrl))]
    private void OpenOnAniList()
    {
        if (AniListUrl is { } url)
        {
            _board.Project.App.Dialogs?.OpenExternalUrl(url);
        }
    }

    /// <summary>Opens the anime's MyAnimeList page in the user's default browser.</summary>
    [RelayCommand(CanExecute = nameof(HasMyAnimeListUrl))]
    private void OpenOnMyAnimeList()
    {
        if (MyAnimeListUrl is { } url)
        {
            _board.Project.App.Dialogs?.OpenExternalUrl(url);
        }
    }

    /// <summary>Opens the chosen OP, ED, or promotional video link in the user's default browser.</summary>
    [RelayCommand]
    private void OpenVideo(VideoLinkViewModel? video)
    {
        if (video is not null)
        {
            _board.Project.App.Dialogs?.OpenExternalUrl(video.Url);
        }
    }

    /// <summary>
    /// Plays a YouTube video in the app's sandboxed player. Links the player cannot play open in the default
    /// browser instead.
    /// </summary>
    [RelayCommand]
    private void PlayVideo(VideoLinkViewModel? video)
    {
        if (video is null)
        {
            return;
        }

        var dialogs = _board.Project.App.Dialogs;
        if (video.YouTubeId is { } id)
        {
            dialogs?.ShowVideoPlayer(new VideoPlayerViewModel(video.BadgeText, video.Title, video.Url, id, dialogs));
        }
        else
        {
            dialogs?.OpenExternalUrl(video.Url);
        }
    }

    private async Task FetchFromLiveChartAsync(bool fetchVideos, bool fetchProfile)
    {
        CancelPendingLoads();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_board.Project.Lifetime);
        _fetchCts = cts;

        IsLoadingLiveData = true;
        LiveDataError = string.Empty;
        LiveDataStatus = (fetchVideos, fetchProfile) switch
        {
            (true, true) => "Loading videos, anime details, and external links from LiveChart...",
            (true, false) => "Loading OP/ED and promotional videos from LiveChart...",
            (false, true) => "Loading anime details and external links from LiveChart...",
            _ => string.Empty,
        };

        try
        {
            var outcome = await _board.Project.App.AnimeDetails.FetchAsync(Item, fetchVideos, fetchProfile, RetryBudget.CreateDefault(), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var updated = false;
            if (fetchVideos)
            {
                _lastVideosFailed = outcome.VideosFailure is not null;
                if (outcome.Videos is { } fetchedVideos)
                {
                    _cachedVideos = fetchedVideos;
                    _hasLoadedVideos = true;
                    ApplyVideos(_cachedVideos, autoSelectOtherIfNoThemes: true);
                    updated = true;
                }
            }

            if (fetchProfile)
            {
                _lastProfileFailed = outcome.ProfileFailure is not null;
                if (outcome.ProfileDetails is { } fetchedProfile)
                {
                    // Persist only LiveChart profile data in the item-wide cache; never store collection-specific snapshot fields there.
                    _cachedProfileDetails = fetchedProfile;
                    _hasLoadedProfile = true;
                    ApplyDetails(_cachedProfileDetails.MergeWith(_snapshotDetails));
                    updated = true;
                }
            }

            var errors = new List<string>();
            if (updated)
            {
                var toStore = new AnimeExtraDetails(Item, outcome.RetrievedAtUtc, _cachedProfileDetails, _cachedVideos, _hasLoadedVideos, _hasLoadedProfile);
                await PersistExtrasAsync(toStore);
            }

            if (outcome.VideosFailure is { } vf)
            {
                errors.Add(vf.Kind == SeasonPageFailure.Unreachable
                    ? await NetworkHint.DescribeUnreachableAsync()
                    : $"Videos: {vf.Detail}");
            }

            if (outcome.ProfileFailure is { } pf)
            {
                if (pf.Kind == SeasonPageFailure.Unreachable)
                {
                    if (outcome.VideosFailure?.Kind != SeasonPageFailure.Unreachable)
                    {
                        errors.Add(await NetworkHint.DescribeUnreachableAsync());
                    }
                }
                else
                {
                    errors.Add($"Details: {pf.Detail}");
                }
            }

            LiveDataStatus = string.Empty;
            LiveDataError = errors.Count > 0 ? string.Join(" ", errors) : string.Empty;
            OnPropertyChanged(nameof(HasNoThemeVideos));
            OnPropertyChanged(nameof(HasNoOtherVideos));
        }
        catch (OperationCanceledException)
        {
            LiveDataStatus = string.Empty;
        }
        catch (Exception ex)
        {
            LiveDataStatus = string.Empty;
            LiveDataError = $"Could not load LiveChart data: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_fetchCts, cts))
            {
                _fetchCts = null;
            }

            cts.Dispose();
            IsLoadingLiveData = false;
            ShowOlderSnapshotHint = !HasExtendedDetails;
            SyncSaveState();
        }
    }

    /// <summary>
    /// Persists fetched LiveChart extras to the project database and awaits durable commit confirmation.
    /// If saves are suspended, stages the fetched extras in the project view model so closing the dialog does not lose them.
    /// </summary>
    private async Task PersistExtrasAsync(AnimeExtraDetails extras)
    {
        _rejectedSaveError = string.Empty;
        _expectedCommittedExtras = extras;
        var result = _board.Project.Session.RecordItemExtras(extras);
        switch (result)
        {
            case SessionResult.Accepted:
            case SessionResult.Unchanged:
                _board.Project.ClearPendingItemExtras(Item);
                await _board.Project.Session.FlushAsync();
                break;
            case SessionResult.Suspended:
                _board.Project.StagePendingItemExtras(extras);
                break;
            case SessionResult.Rejected rejected:
                _board.Project.ClearPendingItemExtras(Item);
                _expectedCommittedExtras = null;
                _rejectedSaveError = $"Fetched LiveChart data could not be saved: {rejected.Reason}";
                break;
            default:
                _board.Project.StagePendingItemExtras(extras);
                break;
        }

        SyncSaveState();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _syncQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _syncQueued, 0);
                if (!_disposed)
                {
                    SyncSaveState();
                }
            });
        }
    }

    private void SyncSaveState()
    {
        if (_disposed)
        {
            return;
        }

        var session = _board.Project.Session;
        var hasStaged = _board.Project.HasPendingItemExtras(Item);
        var saveFailed = session.SaveState == SaveState.Failed;
        var durableMatches = _expectedCommittedExtras is null
            || (session.Durable.ItemExtras.TryGetValue(Item, out var durable) && durable == _expectedCommittedExtras);

        if (!hasStaged && durableMatches)
        {
            _expectedCommittedExtras = null;
        }

        if (!string.IsNullOrEmpty(_rejectedSaveError))
        {
            SaveError = _rejectedSaveError;
        }
        else if (hasStaged)
        {
            var detail = session.Failure?.Message;
            SaveError = saveFailed && !string.IsNullOrWhiteSpace(detail)
                ? $"Fetched LiveChart data is not saved yet because saving failed ({detail}). Click Retry save to store pending changes."
                : "Fetched LiveChart data is not saved yet because project saves are suspended. Click Retry save to store pending changes.";
        }
        else if (!durableMatches && saveFailed)
        {
            var detail = session.Failure?.Message;
            SaveError = string.IsNullOrWhiteSpace(detail)
                ? "Could not save fetched LiveChart data to the project. Click Retry save to try again."
                : $"Could not save fetched LiveChart data to the project: {detail}";
        }
        else if (saveFailed)
        {
            var detail = session.Failure?.Message;
            SaveError = string.IsNullOrWhiteSpace(detail)
                ? "Project saving failed and edits are paused. Click Retry save to try again."
                : $"Project saving failed ({detail}). Click Retry save to try again.";
        }
        else
        {
            SaveError = string.Empty;
        }

        HasCachedLiveData = _hasLoadedVideos && _hasLoadedProfile && !hasStaged && durableMatches;
        ShowRetrySaveButton = saveFailed || hasStaged;
        OnPropertyChanged(nameof(EditsEnabled));
        DuplicateEntryCommand.NotifyCanExecuteChanged();
        RemoveThisCopyCommand.NotifyCanExecuteChanged();
        RetrySaveCommand.NotifyCanExecuteChanged();
    }

    private void ApplyDetails(SourceDetails details)
    {
        Rating = details.Rating;
        Premiere = details.Premiere;
        Metadata = details.Metadata;
        Synopsis = details.Synopsis;
        StudiosText = details.Studios.IsEmpty ? null : string.Join(", ", details.Studios);
        Tags = details.Tags;
        AniListUrl = details.AniListUrl;
        MyAnimeListUrl = details.MyAnimeListUrl;
        HasExtendedDetails = Rating is not null || Premiere is not null || Metadata is not null
            || Synopsis is not null || !details.Studios.IsEmpty || !details.Tags.IsEmpty;
        ShowOlderSnapshotHint = !HasExtendedDetails && !IsLoadingLiveData;
    }

    private void ApplyVideos(ImmutableArray<AnimeVideo> videos, bool autoSelectOtherIfNoThemes)
    {
        ThemeVideos.Clear();
        OtherVideos.Clear();

        foreach (var theme in AnimeVideoClassifier.OrderThemeSongs(videos))
        {
            ThemeVideos.Add(new VideoLinkViewModel(theme));
        }

        foreach (var other in videos.Where(v => !AnimeVideoClassifier.IsThemeSong(v.Kind)))
        {
            OtherVideos.Add(new VideoLinkViewModel(other));
        }

        if (autoSelectOtherIfNoThemes && ThemeVideos.Count == 0 && OtherVideos.Count > 0)
        {
            SelectedVideoTab = OtherTab;
        }
        else if (ThemeVideos.Count > 0)
        {
            SelectedVideoTab = ThemesTab;
        }

        OnPropertyChanged(nameof(ThemeVideosTabLabel));
        OnPropertyChanged(nameof(OtherVideosTabLabel));
        OnPropertyChanged(nameof(HasNoThemeVideos));
        OnPropertyChanged(nameof(HasNoOtherVideos));
    }

    private void RefreshCopies(ProjectState state)
    {
        Copies.Clear();
        if (!state.Boards.TryGetValue(_board.Id, out var boardState))
        {
            TotalCopiesCount = 0;
            CopySummary = string.Empty;
            return;
        }

        var copies = boardState.CopiesOf(Item);
        TotalCopiesCount = copies.Length;
        foreach (var copy in copies)
        {
            var tierId = boardState.TierOf(copy.Key);
            var location = tierId is { } tid
                ? $"Tier {boardState.Tiers.First(t => t.Id == tid).Name}"
                : "Unranked";
            var label = copy.IsDuplicate ? $"Copy #{copy.CopyNumber}" : "Original";
            Copies.Add($"{label} — {location}");
        }

        var thisTier = boardState.TierOf(Key);
        var thisLocation = thisTier is { } id
            ? $"Tier {boardState.Tiers.First(t => t.Id == id).Name}"
            : "Unranked";
        CopySummary = IsDuplicate
            ? $"Copy #{CopyNumber} of {TotalCopiesCount} · {thisLocation}"
            : TotalCopiesCount > 1
                ? $"Original (1 of {TotalCopiesCount} copies) · {thisLocation}"
                : $"Original · {thisLocation}";
    }
}
