using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>One collection entry as shown in the poster grid or list view. The thumbnail loads lazily when displayed.</summary>
public sealed partial class EntryViewModel : ObservableObject
{
    private readonly CollectionViewModel _owner;
    private AssetRecord? _asset;
    private AssetHash? _requested;
    private int _requestedHeight;
    private AssetHash? _failedHash;
    private bool _included;

    [ObservableProperty]
    private int _position;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _titleNote;

    [ObservableProperty]
    private bool _hasCustomTitle;

    [ObservableProperty]
    private bool _sourceMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListCoverStatus), nameof(ListCoverStatusToolTip))]
    private string _coverNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListCoverStatus), nameof(ListCoverStatusToolTip))]
    private bool _hasCover;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListCoverStatus), nameof(ListCoverStatusToolTip))]
    private bool _coverBroken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListCoverStatus), nameof(ListCoverStatusToolTip))]
    private bool _coverNeedsReview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListCoverStatus), nameof(ListCoverStatusToolTip))]
    private bool _coverUnavailable;

    [ObservableProperty]
    private string _coverPlaceholder = "No cover";

    [ObservableProperty]
    private string _statusBadgeText = string.Empty;

    [ObservableProperty]
    private string _observedDisplayed = string.Empty;

    [ObservableProperty]
    private string? _observedEnglish;

    [ObservableProperty]
    private string? _observedRomanized;

    [ObservableProperty]
    private string? _observedNative;

    [ObservableProperty]
    private string _accessibleName = string.Empty;

    [ObservableProperty]
    private string _itemStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsSummary))]
    private AnimeFacts _facts = AnimeFacts.Unknown;

    private ImageSource? _thumbnail;

    /// <summary>Creates an entry view model bound to a LiveChart identity within a collection.</summary>
    public EntryViewModel(CollectionViewModel owner, LiveChartId item)
    {
        _owner = owner;
        Item = item;
    }

    public LiveChartId Item { get; }

    public string IdText => Item.Value.ToString(CultureInfo.InvariantCulture);

    public string PositionText => $"#{Position}";

    public string SearchText { get; private set; } = string.Empty;

    /// <summary>One line for the list view, such as "Manga · Rating 8.12 · Has OP"; empty while nothing is known.</summary>
    public string DetailsSummary => string.Join(" · ", new[]
    {
        Facts.SourceMaterial,
        Facts.Rating is { } rating ? $"Rating {rating.ToString("0.00", CultureInfo.CurrentCulture)}" : null,
        Facts.HasOpening switch { true => "Has OP", false => "No OP", null => null },
    }.Where(p => p is not null));

    public bool NeedsAttention => CoverBroken || CoverNeedsReview || CoverUnavailable || SourceMissing;

    /// <summary>Concise cover status label derived strictly from explicit state (never misclassifying notes like "Cover repaired").</summary>
    public string ListCoverStatus => CoverBroken
        ? "Damaged"
        : CoverNeedsReview
            ? "Needs review"
            : CoverUnavailable
                ? "Unavailable"
                : HasCover ? "OK" : "No cover";

    /// <summary>Detailed cover recovery guidance shown as a tooltip on list rows.</summary>
    public string ListCoverStatusToolTip => !string.IsNullOrWhiteSpace(CoverNote)
        ? CoverNote
        : HasCover
            ? "Cover file is intact."
            : "No cover downloaded. Use Fetch covers to download from LiveChart.";

    /// <summary>Two-way bound to the include checkbox. A user change runs the include/exclude command.</summary>
    public bool Included
    {
        get => _included;
        set
        {
            if (value != _included && _owner.SetIncluded(Item, value))
            {
                _included = value;
            }

            OnPropertyChanged();
            UpdateAccessibleName();
        }
    }

    /// <summary>Thumbnail sized for the collection's current presentation density (HiDPI bucketed).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            var desiredHeight = _owner.DesiredThumbnailHeight;
            if (_asset is { } asset && (_requested != asset.Hash || _requestedHeight != desiredHeight))
            {
                _requested = asset.Hash;
                _requestedHeight = desiredHeight;
                _ = LoadThumbnailAsync(asset, desiredHeight);
            }

            return _thumbnail;
        }
    }

    /// <summary>Notifies bindings to re-request higher-resolution thumbnails when grid density changes.</summary>
    internal void RefreshThumbnailResolution()
    {
        if (_asset is not null && _requestedHeight != _owner.DesiredThumbnailHeight)
        {
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    /// <summary>
    /// Restarts thumbnail loading after cover fetch/repair (even for offscreen virtualized items) while retaining
    /// unavailable state until <see cref="LoadThumbnailAsync"/> confirms a decoded bitmap.
    /// </summary>
    internal void ResetThumbnailFailure()
    {
        if (_failedHash is null && !CoverUnavailable)
        {
            return;
        }

        _requested = null;
        _requestedHeight = 0;
        _thumbnail = null;
        if (_asset is { } asset)
        {
            var desiredHeight = _owner.DesiredThumbnailHeight;
            _requested = asset.Hash;
            _requestedHeight = desiredHeight;
            _ = LoadThumbnailAsync(asset, desiredHeight);
        }

        OnPropertyChanged(nameof(Thumbnail));
    }

    /// <summary>
    /// Applies the entry's current state. A selected cover whose file is known to be missing or damaged
    /// (<paramref name="coverBroken"/>) shows a placeholder instead of a cached thumbnail, so the UI never presents
    /// an unusable cover as valid.
    /// </summary>
    internal void Update(int position, CollectionEntry entry, ResolvedTitle title, SourceTitles observed, AssetRecord? asset, CoverEntryStatus? coverStatus, bool coverBroken, AnimeFacts? facts = null)
    {
        Position = position;
        Facts = facts ?? AnimeFacts.Unknown;
        OnPropertyChanged(nameof(PositionText));
        Title = title.Text;
        HasCustomTitle = title.Source == TitleSource.UserOverride;
        TitleNote = title.Source switch
        {
            TitleSource.UserOverride => "Custom title",
            _ when title.IsLanguageFallback => "Selected language unavailable; showing LiveChart's title",
            _ => null,
        };
        ObservedDisplayed = observed.Displayed;
        ObservedEnglish = observed.English;
        ObservedRomanized = observed.Romanized;
        ObservedNative = observed.Native;
        SourceMissing = entry.SourceMissing;
        if (_included != entry.Included)
        {
            _included = entry.Included;
            OnPropertyChanged(nameof(Included));
        }

        SearchText = string.Join('\n', new[] { title.Text, observed.Displayed, observed.English, observed.Romanized, observed.Native, IdText }.Where(t => t is not null));
        var broken = asset is not null && coverBroken;
        var shown = broken ? null : asset;
        if (shown?.Hash != _failedHash)
        {
            _failedHash = null;
        }

        var unavailable = shown is not null && _failedHash == shown.Hash;
        CoverBroken = broken;
        CoverNeedsReview = coverStatus?.Status == CoverStatus.NeedsReview && asset is not null;
        CoverUnavailable = unavailable;
        HasCover = shown is not null && !unavailable;
        CoverPlaceholder = broken ? "Damaged cover" : unavailable ? "Unavailable" : "No cover";
        CoverNote = unavailable
            ? "Cover file unavailable"
            : coverStatus switch
            {
                { Status: CoverStatus.NeedsReview } when asset is not null => "Cover damaged; LiveChart offers a different one. Select the entry to review it.",
                { Status: CoverStatus.Failed } status when asset is not null => "Cover damaged; repair failed: " + status.Detail,
                _ when broken => "Cover file missing or damaged. Select the entry to recover it.",
                { Status: CoverStatus.Repaired } when asset is not null => "Cover repaired",
                _ when asset is not null => string.Empty,
                { Status: CoverStatus.Pending } => "Waiting for cover...",
                { Status: CoverStatus.Missing } => "No usable cover from LiveChart",
                { Status: CoverStatus.Failed or CoverStatus.NeedsReview } status => "Cover failed: " + status.Detail,
                { Status: CoverStatus.Paused } => "Cover paused",
                { Status: CoverStatus.Cancelled } => "Cover cancelled",
                _ => "No cover yet",
            };
        StatusBadgeText = broken ? "Damaged"
            : SourceMissing ? "Unlisted"
            : coverStatus?.Status == CoverStatus.Pending ? "Fetching"
            : string.Empty;
        OnPropertyChanged(nameof(NeedsAttention));
        OnPropertyChanged(nameof(ListCoverStatus));
        UpdateAccessibleName();

        if (_asset?.Hash != shown?.Hash)
        {
            _asset = shown;
            _requested = null;
            _requestedHeight = 0;
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    private void UpdateAccessibleName()
    {
        // Keep exact title accessible name for UIA checks while exposing full status clearly via ItemStatus.
        AccessibleName = Title;
        var parts = new List<string>
        {
            PositionText,
            _included ? "Included" : "Excluded",
        };
        if (SourceMissing)
        {
            parts.Add("No longer listed by LiveChart");
        }

        if (!string.IsNullOrWhiteSpace(TitleNote))
        {
            parts.Add(TitleNote);
        }

        if (!string.IsNullOrWhiteSpace(CoverNote))
        {
            parts.Add(CoverNote);
        }

        ItemStatus = string.Join(", ", parts);
    }

    /// <summary>Loads off the dispatcher; failures leave the placeholder. Stale completions are ignored.</summary>
    private async Task LoadThumbnailAsync(AssetRecord asset, int pixelHeight)
    {
        try
        {
            var image = await _owner.Services.Thumbnails.LoadAsync(asset, pixelHeight, _owner.Project.Lifetime);
            if (_asset?.Hash != asset.Hash || _requestedHeight != pixelHeight)
            {
                return;
            }

            if (image is null)
            {
                MarkThumbnailUnavailable(asset.Hash);
                return;
            }

            var wasUnavailable = CoverUnavailable;
            _failedHash = null;
            CoverUnavailable = false;
            HasCover = true;
            if (wasUnavailable)
            {
                CoverPlaceholder = "No cover";
                CoverNote = string.Empty;
                StatusBadgeText = SourceMissing ? "Unlisted" : string.Empty;
                OnPropertyChanged(nameof(NeedsAttention));
                UpdateAccessibleName();
                _owner.NotifyEntryThumbnailStateChanged();
            }

            _thumbnail = image;
            OnPropertyChanged(nameof(Thumbnail));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException or COMException or ArgumentException)
        {
            if (_asset?.Hash != asset.Hash || _requestedHeight != pixelHeight)
            {
                return;
            }

            MarkThumbnailUnavailable(asset.Hash);
        }
    }

    private void MarkThumbnailUnavailable(AssetHash hash)
    {
        _failedHash = hash;
        CoverUnavailable = true;
        HasCover = false;
        CoverPlaceholder = "Unavailable";
        CoverNote = "Cover file unavailable";
        StatusBadgeText = SourceMissing ? "Unlisted" : string.Empty;
        OnPropertyChanged(nameof(NeedsAttention));
        UpdateAccessibleName();
        _owner.NotifyEntryThumbnailStateChanged();
    }
}
