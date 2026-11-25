using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using AnimeTierlist.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>One tier row of a board, or the unranked pool when <see cref="Id"/> is null.</summary>
public sealed partial class TierViewModel : ObservableObject
{
    private static readonly Brush PoolBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x28, 0x2E, 0x3A)));
    private static readonly Brush DarkInkBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x00, 0x00)));
    private static readonly Brush LightInkBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF)));

    private readonly BoardViewModel _owner;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _header = string.Empty;

    [ObservableProperty]
    private string _countText = "0";

    [ObservableProperty]
    private string _shortcutText = string.Empty;

    [ObservableProperty]
    private bool _hasShortcut;

    [ObservableProperty]
    private Brush _background = PoolBrush;

    [ObservableProperty]
    private Brush _headerForeground = LightInkBrush;

    [ObservableProperty]
    private TierColor _color;

    [ObservableProperty]
    private bool _isDragTarget;

    [ObservableProperty]
    private bool _isSelectedTier;

    [ObservableProperty]
    private BoardEntryViewModel? _selectedEntry;

    /// <summary>Creates a tier row or the unranked pool row when <paramref name="id"/> is null.</summary>
    public TierViewModel(BoardViewModel owner, TierId? id, string name)
    {
        _owner = owner;
        Id = id;
        _name = name;
    }

    public TierId? Id { get; }

    public bool IsPool => Id is null;

    public bool IsEmpty => Entries.Count == 0;

    public string EmptyHint => IsPool
        ? "All shows on this board are ranked."
        : HasShortcut
            ? $"Drop shows here or press {ShortcutText} with a show selected"
            : "Drop shows here or use Move to tier";

    public ObservableCollection<BoardEntryViewModel> Entries { get; } = [];

    /// <summary>Screen readers and ComboBox selection display announce the tier name.</summary>
    public override string ToString() => Name;

    /// <summary>Screen readers hear the tier by name and size; color only supplements the name.</summary>
    public string AccessibleName => IsPool ? $"Unranked, {Entries.Count} shows" : $"Tier {Name}, {Entries.Count} shows";

    internal void Update(string name, TierColor? color, int? shortcutIndex = null)
    {
        Name = name;
        if (color is { } c)
        {
            Color = c;
            Background = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(c.Red, c.Green, c.Blue)));
            HeaderForeground = c.PreferredInk() == TierInk.White ? LightInkBrush : DarkInkBrush;
        }

        if (IsPool)
        {
            ShortcutText = "0";
            HasShortcut = true;
        }
        else if (shortcutIndex is >= 1 and <= 9)
        {
            ShortcutText = shortcutIndex.Value.ToString(CultureInfo.InvariantCulture);
            HasShortcut = true;
        }
        else
        {
            ShortcutText = string.Empty;
            HasShortcut = false;
        }

        CountText = Entries.Count == 1 ? "1 show" : $"{Entries.Count} shows";
        Header = IsPool ? $"Unranked ({Entries.Count})" : $"{Name} ({Entries.Count})";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(AccessibleName));
    }

    partial void OnSelectedEntryChanged(BoardEntryViewModel? value)
    {
        if (value is not null)
        {
            _owner.OnEntrySelected(this, value);
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>One board entry: its pinned title and cover. The thumbnail loads lazily at HiDPI resolution when first shown.</summary>
public sealed partial class BoardEntryViewModel : ObservableObject
{
    private readonly BoardViewModel _owner;
    private AssetRecord? _asset;
    private AssetHash? _requested;
    private int _requestedHeight;
    private AssetHash? _failedHash;
    private ImageSource? _thumbnail;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _hasCover;

    [ObservableProperty]
    private bool _isBrokenCover;

    [ObservableProperty]
    private string _coverPlaceholder = "No cover";

    [ObservableProperty]
    private string _placementLabel = string.Empty;

    [ObservableProperty]
    private string _accessibleName = string.Empty;

    [ObservableProperty]
    private bool _isDimmed;

    [ObservableProperty]
    private bool _isSearchMatch;

    [ObservableProperty]
    private string _itemStatus = string.Empty;

    /// <summary>Creates a board entry view model keyed by LiveChart identity and copy number.</summary>
    public BoardEntryViewModel(BoardViewModel owner, BoardEntryKey key)
    {
        _owner = owner;
        Key = key;
    }

    public BoardEntryKey Key { get; }

    public LiveChartId Item => Key.Item;

    public int CopyNumber => Key.CopyNumber;

    public bool IsDuplicate => Key.IsDuplicate;

    public string CopyBadgeLabel => IsDuplicate ? $"Copy #{CopyNumber}" : "Original";

    public string IdText => Item.Value.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>Notifies bindings to load a sharper thumbnail bucket when card density changes.</summary>
    internal void RefreshThumbnailResolution()
    {
        if (_asset is not null && _requestedHeight != _owner.DesiredThumbnailHeight)
        {
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    /// <summary>
    /// Restarts thumbnail loading after cover recheck/repair (even for offscreen virtualized cards) while retaining
    /// unavailable state until <see cref="LoadThumbnailAsync"/> confirms a decoded bitmap.
    /// </summary>
    internal void ResetThumbnailFailure()
    {
        if (_failedHash is null)
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

    internal void UpdateSearchHighlight(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            IsDimmed = false;
            IsSearchMatch = false;
        }
        else
        {
            var match = Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || IdText.Contains(query, StringComparison.OrdinalIgnoreCase);
            IsSearchMatch = match;
            IsDimmed = !match;
        }

        UpdateItemStatus();
    }

    /// <summary>Tooltip for board cards explaining pinned-cover recovery when a card has no cover.</summary>
    public string CoverToolTip
    {
        get
        {
            var titleWithCopy = IsDuplicate ? $"{Title} (Copy #{CopyNumber})" : Title;
            return HasCover
                ? $"{titleWithCopy} — Double-click for LiveChart details or to duplicate"
                : IsBrokenCover
                    ? $"{titleWithCopy} — Pinned cover file is missing or damaged. Repair covers in the source collection, then use Update board → Fill missing or damaged covers."
                    : $"{titleWithCopy} — No cover pinned on this board. Repair or fetch covers in the source collection, then use Update board → Fill missing or damaged covers.";
        }
    }

    internal void Update(BoardEntry entry, AssetRecord? asset, bool broken, string placement)
    {
        Title = entry.PinnedTitle;
        PlacementLabel = IsDuplicate ? $"Copy #{CopyNumber} · {placement}" : placement;
        var shown = broken ? null : asset;
        if (shown?.Hash != _failedHash)
        {
            _failedHash = null;
        }

        var unavailable = shown is not null && _failedHash == shown.Hash;
        HasCover = shown is not null && !unavailable;
        IsBrokenCover = (asset is not null && broken) || unavailable;
        CoverPlaceholder = asset is not null && broken
            ? "Damaged cover"
            : unavailable
                ? "Unavailable"
                : "No cover";
        UpdateAccessibilityAndTooltip();

        if (_asset?.Hash != shown?.Hash)
        {
            _asset = shown;
            _requested = null;
            _requestedHeight = 0;
            _thumbnail = null;
            OnPropertyChanged(nameof(Thumbnail));
        }
    }

    private void UpdateAccessibilityAndTooltip()
    {
        var copyText = IsDuplicate ? $" (Copy {CopyNumber})" : string.Empty;
        AccessibleName = $"{Title}{copyText}, {PlacementLabel}" + (HasCover ? string.Empty : $", {CoverPlaceholder.ToLowerInvariant()}");
        OnPropertyChanged(nameof(CoverToolTip));
        UpdateItemStatus();
    }

    private void UpdateItemStatus()
    {
        var parts = new List<string>();
        if (IsDimmed)
        {
            parts.Add("not matching find filter");
        }

        if (!HasCover)
        {
            parts.Add(CoverPlaceholder.ToLowerInvariant());
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

            _failedHash = null;
            HasCover = true;
            IsBrokenCover = false;
            CoverPlaceholder = "No cover";
            _thumbnail = image;
            OnPropertyChanged(nameof(Thumbnail));
            UpdateAccessibilityAndTooltip();
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
        HasCover = false;
        IsBrokenCover = true;
        CoverPlaceholder = "Unavailable";
        UpdateAccessibilityAndTooltip();
    }
}
