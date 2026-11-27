using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Data;
using System.Windows.Media;
using AnimeTierlist.Application.Acquisition;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Catalogue;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Infrastructure.Exports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EntryCoverStatus = AnimeTierlist.Application.Covers.CoverStatus;

namespace AnimeTierlist.Desktop.ViewModels;

public sealed record LanguageOption(string Label, TitleLanguage Value)
{
    /// <summary>Screen readers and UI Automation announce the label, not the record shape.</summary>
    public override string ToString() => Label;
}

/// <summary>Visual filter chips for collection curation. Filters affect display only, never stored order or exports.</summary>
public enum CollectionFilterMode
{
    All,
    Included,
    Excluded,
    NoCover,
    NeedsAttention,
}

/// <summary>
/// One choice in a details filter list. <see cref="Value"/> is <see cref="AnyValue"/> for no filter and
/// <see cref="UnknownValue"/> for shows without the detail; the label carries the matching show count.
/// </summary>
public sealed record FilterOption(string Value, string Label)
{
    public const string AnyValue = "*";
    public const string UnknownValue = "?";

    /// <summary>Screen readers and UI Automation announce the label, not the record shape.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Curation of one collection: inclusion, explicit manual order, title language and overrides, cover acquisition,
/// reviewed cover recovery, reviewed refresh, and deletion. Search and filter chips only filter the view;
/// they never change membership, order, or export scope.
/// </summary>
public sealed partial class CollectionViewModel : ObservableObject
{
    private readonly ProjectViewModel _project;
    private readonly Dictionary<LiveChartId, CoverEntryStatus> _coverStatuses = [];
    private bool _updating;
    private int _inspection;
    private CoverProvenance? _inspectedCover;
    private CoverReplacement? _offer;
    private string? _loadedOverride;
    private readonly HashSet<AssetHash> _checkedAssets = [];
    private readonly HashSet<AssetHash> _brokenAssets = [];
    private bool _scanning;
    private bool _showingDamageSummary;
    private bool _initialized;
    private bool _rebuildingFilterOptions;
    private string _openingFilter = FilterOption.AnyValue;
    private string _sourceFilter = FilterOption.AnyValue;
    private string _formatFilter = FilterOption.AnyValue;
    private string _genreFilter = FilterOption.AnyValue;
    private string _studioFilter = FilterOption.AnyValue;
    private string _ratingFilter = FilterOption.AnyValue;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _contextLabel = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered), nameof(CanReorder), nameof(FilterSummary), nameof(HasSearchText))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(MoveToTopCommand), nameof(MoveToBottomCommand))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered), nameof(CanReorder), nameof(FilterSummary))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(MoveToTopCommand), nameof(MoveToBottomCommand))]
    private CollectionFilterMode _filterMode = CollectionFilterMode.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllChipLabel))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncludedChipLabel))]
    private int _includedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcludedChipLabel))]
    private int _excludedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoCoverChipLabel))]
    private int _noCoverCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttentionChipLabel))]
    private int _needsAttentionCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterSummary), nameof(IncludeVisibleLabel), nameof(ExcludeVisibleLabel))]
    private int _visibleCount;

    [ObservableProperty]
    private bool _showDetailFilters;

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _openingOptions = [new(FilterOption.AnyValue, "Any OP status")];

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _sourceOptions = [new(FilterOption.AnyValue, "All sources")];

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _formatOptions = [new(FilterOption.AnyValue, "All formats")];

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _genreOptions = [new(FilterOption.AnyValue, "All genres")];

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _studioOptions = [new(FilterOption.AnyValue, "All studios")];

    [ObservableProperty]
    private IReadOnlyList<FilterOption> _ratingOptions = [new(FilterOption.AnyValue, "Any rating")];

    [ObservableProperty]
    private int _missingDetailsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineFetchDetails))]
    private bool _isFetchingDetails;

    [ObservableProperty]
    private string _detailsStatus = string.Empty;

    [ObservableProperty]
    private int _detailsProgressCompleted;

    [ObservableProperty]
    private int _detailsProgressTotal = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridView), nameof(IsListView))]
    private CollectionViewMode _viewMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveDensity), nameof(TileWidth), nameof(TileHeight), nameof(PosterHeight), nameof(PosterWidth), nameof(DesiredThumbnailHeight))]
    private CardDensity _density;

    /// <summary>0 = normal viewport, 1 = compact (&lt;260 DIP), 2 = ultra-compact (&lt;225 DIP, e.g. 960x540 with cover + save-failed banners).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompactViewport), nameof(IsNotCompactViewport), nameof(EffectiveDensity), nameof(TileWidth), nameof(TileHeight), nameof(PosterHeight), nameof(PosterWidth), nameof(DesiredThumbnailHeight))]
    private int _compactViewportLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInlineBulkActions), nameof(ShowInlineFetchDetails))]
    private bool _isNarrowToolbar;

    [ObservableProperty]
    private bool _inspectorOpen;

    [ObservableProperty]
    private bool _isRenamingInline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSelectedCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(MoveToTopCommand), nameof(MoveToBottomCommand), nameof(SetTitleOverrideCommand), nameof(ClearTitleOverrideCommand))]
    private EntryViewModel? _selectedEntry;

    [ObservableProperty]
    private string _selectedReferencingBoards = "None";

    [ObservableProperty]
    private LanguageOption _language;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetTitleOverrideCommand))]
    private string _titleOverrideText = string.Empty;

    [ObservableProperty]
    private string _coverStatus = string.Empty;

    [ObservableProperty]
    private bool _isFetchingCovers;

    [ObservableProperty]
    private int _coverProgressCompleted;

    [ObservableProperty]
    private int _coverProgressTotal = 1;

    [ObservableProperty]
    private string _refreshStatus = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _selectedCoverText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBoardCommand))]
    private string _newBoardName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MarkCoverMissingCommand), nameof(UseNewCoverCommand))]
    private bool _selectedCoverBroken;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseNewCoverCommand))]
    private ImageSource? _offerThumbnail;

    /// <summary>Creates a collection curation view model initialized from portable UI preferences.</summary>
    public CollectionViewModel(ProjectViewModel project, CollectionId id)
    {
        _project = project;
        Id = id;
        _language = LanguageOptions[0];
        _viewMode = project.App.Preferences.Current.CollectionViewMode;
        _density = project.App.Preferences.Current.CollectionDensity;
        _inspectorOpen = project.App.Preferences.Current.CollectionInspectorOpen;
        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = Matches;
        _initialized = true;
    }

    /// <summary>Raised after a keyboard order move so the view can scroll and keep keyboard focus on the moved entry.</summary>
    public event EventHandler<EntryViewModel>? EntryMoved;

    /// <summary>Raised when the user changes search text or filter chip so the view resets scroll to the top of results.</summary>
    public event EventHandler? FilterOrSearchChanged;

    public CollectionId Id { get; }

    public ProjectViewModel Project => _project;

    /// <summary>Formatted filter chip labels so UI Automation announces full text rather than raw numbers.</summary>
    public string AllChipLabel => $"All ({TotalCount})";

    public string IncludedChipLabel => $"Included ({IncludedCount})";

    public string ExcludedChipLabel => $"Excluded ({ExcludedCount})";

    public string NoCoverChipLabel => $"No cover ({NoCoverCount})";

    public string AttentionChipLabel => $"Attention ({NeedsAttentionCount})";

    public string IncludeVisibleLabel => $"Include shown ({VisibleCount})";

    public string ExcludeVisibleLabel => $"Exclude shown ({VisibleCount})";

    /// <summary>Filter by LiveChart's opening videos: any, has an OP, no OP, or videos not fetched yet.</summary>
    public string OpeningFilter
    {
        get => _openingFilter;
        set => SetDetailFilter(ref _openingFilter, value);
    }

    public string SourceFilter
    {
        get => _sourceFilter;
        set => SetDetailFilter(ref _sourceFilter, value);
    }

    public string FormatFilter
    {
        get => _formatFilter;
        set => SetDetailFilter(ref _formatFilter, value);
    }

    public string GenreFilter
    {
        get => _genreFilter;
        set => SetDetailFilter(ref _genreFilter, value);
    }

    public string StudioFilter
    {
        get => _studioFilter;
        set => SetDetailFilter(ref _studioFilter, value);
    }

    /// <summary>A minimum LiveChart rating such as <c>7.5</c>, or unrated shows only.</summary>
    public string RatingFilter
    {
        get => _ratingFilter;
        set => SetDetailFilter(ref _ratingFilter, value);
    }

    public int ActiveDetailFilterCount =>
        new[] { _openingFilter, _sourceFilter, _formatFilter, _genreFilter, _studioFilter, _ratingFilter }.Count(f => f != FilterOption.AnyValue);

    public bool HasDetailFilters => ActiveDetailFilterCount > 0;

    public string DetailFiltersLabel => HasDetailFilters ? $"Filters ({ActiveDetailFilterCount})" : "Filters";

    public string FetchDetailsToolTip => MissingDetailsCount == 0
        ? "Details and videos for every show are saved in this project."
        : $"Fetch LiveChart details and videos for the {MissingDetailsCount} shows that do not have them yet, one page every 5 seconds (about {EstimateDuration(MissingDetailsCount)}).";

    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    public ICollectionView View { get; }

    public bool HasSelectedEntry => SelectedEntry is not null;

    /// <summary>True while the background check of selected cover files runs. Tests wait for it before changing cover files.</summary>
    internal bool IsCheckingCovers => _scanning;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsFiltered => FilterMode != CollectionFilterMode.All || !string.IsNullOrWhiteSpace(SearchText) || HasDetailFilters;

    public bool CanReorder => !IsFiltered && !_project.EditsSuspended;

    public bool IsGridView => ViewMode == CollectionViewMode.Grid;

    public bool IsListView => ViewMode == CollectionViewMode.List;

    public bool IsCompactViewport => CompactViewportLevel > 0;

    /// <summary>Effective card density (steps down to Compact on short viewports without altering saved Density).</summary>
    public CardDensity EffectiveDensity => IsCompactViewport ? CardDensity.Compact : Density;

    public bool IsNotCompactViewport => CompactViewportLevel == 0;

    /// <summary>When narrow (&lt;1000 DIP), Include/Exclude all fold cleanly into the overflow menu so the filter bar stays single-row.</summary>
    public bool ShowInlineBulkActions => !IsNarrowToolbar;

    /// <summary>The inline Fetch details button folds into the overflow menu on narrow viewports and gives way to Stop while running.</summary>
    public bool ShowInlineFetchDetails => !IsNarrowToolbar && !IsFetchingDetails;

    public double TileWidth => IsCompactViewport
        ? 108
        : EffectiveDensity switch
        {
            CardDensity.Compact => 116,
            CardDensity.Large => 168,
            _ => 142,
        };

    public double PosterHeight => CompactViewportLevel >= 2
        ? 112
        : CompactViewportLevel == 1
            ? 126
            : EffectiveDensity switch
            {
                CardDensity.Compact => 130,
                CardDensity.Large => 204,
                _ => 170,
            };

    /// <summary>Matches the 2:3 poster aspect ratio in ultra-compact viewports so short mats do not show dark pillarbox bars.</summary>
    public double PosterWidth => CompactViewportLevel >= 2
        ? Math.Round(PosterHeight * 0.70)
        : double.NaN;

    /// <summary>
    /// Total vertical card chrome outside the poster art box: SelectionFrame (2.5*2 + 1*2 = 7)
    /// + outer card border/padding (1.5*2 + 6*2 = 15) + caption margin/height (6 + 50 = 56).
    /// </summary>
    public const double CardVerticalChrome = 78;

    public double TileHeight => PosterHeight + CardVerticalChrome;

    private double _dpiScale = 1.0;

    /// <summary>Current monitor vertical DPI scale reported by the view (defaults to 1.0 at 96 DPI).</summary>
    public double DpiScale => _dpiScale;

    /// <summary>Updates the monitor DPI scale and refreshes thumbnails when the physical resolution bucket changes.</summary>
    public void SetDpiScale(double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
        {
            return;
        }

        var previousBucket = DesiredThumbnailHeight;
        _dpiScale = dpiScale;
        if (DesiredThumbnailHeight != previousBucket)
        {
            foreach (var entry in Entries)
            {
                entry.RefreshThumbnailResolution();
            }
        }
    }

    /// <summary>Target physical thumbnail height bucket derived from the rendered poster height and monitor DPI.</summary>
    public int DesiredThumbnailHeight => ThumbnailLoader.BucketHeight(
        (int)Math.Ceiling((ViewMode == CollectionViewMode.List ? 60.0 : PosterHeight) * _dpiScale));

    public string FilterSummary => IsFiltered
        ? $"Showing {VisibleCount} of {TotalCount} shows ({IncludedCount} included). Visual filter only; collection order and export scope are unchanged." +
          (HasDetailFilters && MissingDetailsCount > 0 ? $" {MissingDetailsCount} shows have no fetched details yet; Fetch details checks them." : string.Empty)
        : string.Empty;

    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
    [
        new("LiveChart default", TitleLanguage.SourceDefault),
        new("English", TitleLanguage.English),
        new("Romaji", TitleLanguage.Romanized),
        new("Native", TitleLanguage.Native),
    ];

    internal ProjectServices Services => _project.Services;

    /// <summary>Applies the current project revision to the view, keeping entry view models (and loaded thumbnails) stable.</summary>
    public void Refresh(ProjectState state)
    {
        if (!state.Collections.TryGetValue(Id, out var collection))
        {
            return;
        }

        _updating = true;
        try
        {
            Name = collection.Name;
            ContextLabel = ProjectViewModel.Describe(collection.Context);
            Language = LanguageOptions.First(l => l.Value == collection.Language);
            var included = collection.Entries.Count(e => e.Included);
            var missing = collection.Entries.Count(e => e.SourceMissing);
            TotalCount = collection.Entries.Length;
            IncludedCount = included;
            ExcludedCount = TotalCount - included;
            Summary = $"{included} of {collection.Entries.Length} included" + (missing > 0 ? $" · {missing} no longer listed by LiveChart" : string.Empty);

            var existing = Entries.ToDictionary(e => e.Item);
            var noCover = 0;
            var attention = 0;
            for (var i = 0; i < collection.Entries.Length; i++)
            {
                var entry = collection.Entries[i];
                if (!existing.Remove(entry.Item, out var view))
                {
                    view = new EntryViewModel(this, entry.Item);
                    Entries.Insert(i, view);
                }
                else
                {
                    var from = Entries.IndexOf(view);
                    if (from != i)
                    {
                        Entries.Move(from, i);
                    }
                }

                var observed = state.Observation(entry.AcceptedOccurrence);
                var asset = entry.Cover is { } cover && state.Assets.TryGetValue(cover.Asset, out var record) ? record : null;
                var broken = asset is not null && _brokenAssets.Contains(asset.Hash);
                var facts = AnimeFacts.From(observed.Details, state.ItemExtras.GetValueOrDefault(entry.Item));
                view.Update(i + 1, entry, state.ResolveTitle(collection, entry), observed.Titles, asset, _coverStatuses.GetValueOrDefault(entry.Item), broken, facts);
                if (!view.HasCover)
                {
                    noCover++;
                }

                if (view.NeedsAttention)
                {
                    attention++;
                }
            }

            foreach (var removed in existing.Values)
            {
                Entries.Remove(removed);
            }

            NoCoverCount = noCover;
            NeedsAttentionCount = attention;
            MissingDetailsCount = collection.Entries.Count(e => AnimeDetailsAcquisition.NeedsDetails(state, e.Item));
            RebuildDetailFilterOptions();
            if (IsFiltered)
            {
                View.Refresh();
            }

            UpdateVisibleCount();
            UpdateReferencingBoards(state);
            NotifyMutationCommands();

            if (_showingDamageSummary)
            {
                var damaged = collection.Entries.Count(e => e.Cover is { } c && _brokenAssets.Contains(c.Asset));
                CoverStatus = damaged == 0 ? string.Empty
                    : $"{damaged} {(damaged == 1 ? "entry has a" : "entries have")} missing or damaged cover file{(damaged == 1 ? string.Empty : "s")}. Fetch covers to restore the exact originals, or select an entry to mark its cover missing.";
            }

            var toCheck = collection.Entries.Where(e => e.Cover is { } c && !_checkedAssets.Contains(c.Asset)).Select(e => state.Assets[e.Cover!.Asset]).Distinct().ToList();
            if (toCheck.Count > 0 && !_scanning)
            {
                _ = CheckCoverFilesAsync(toCheck);
            }

            if (SelectedEntry is { } selected && collection.IndexOf(selected.Item) >= 0)
            {
                var saved = collection.Entry(selected.Item).TitleOverride;
                if (saved != _loadedOverride)
                {
                    // Only a saved change replaces the text box, so progress and save notifications never discard typing.
                    _loadedOverride = saved;
                    TitleOverrideText = saved ?? string.Empty;
                }

                if (collection.Entry(selected.Item).Cover != _inspectedCover)
                {
                    _ = InspectSelectedCoverAsync();
                }
            }
        }
        finally
        {
            _updating = false;
        }
    }

    internal bool SetIncluded(LiveChartId item, bool included) =>
        Edit(included ? "Include entry" : "Exclude entry", c => CollectionOperations.SetIncluded(c, [item], included));

    /// <summary>Updates cover-count chips and active cover-status filters when an entry's async thumbnail load fails or recovers.</summary>
    internal void NotifyEntryThumbnailStateChanged()
    {
        NoCoverCount = Entries.Count(e => !e.HasCover);
        NeedsAttentionCount = Entries.Count(e => e.NeedsAttention);
        if (FilterMode is CollectionFilterMode.NoCover or CollectionFilterMode.NeedsAttention)
        {
            View.Refresh();
        }

        UpdateVisibleCount();
    }

    /// <summary>Toggles inclusion for one or more selected entries as a single undoable command.</summary>
    public bool ToggleEntries(IReadOnlyList<EntryViewModel> entries)
    {
        if (entries.Count == 0)
        {
            return false;
        }

        if (entries.Count == 1)
        {
            return SetIncluded(entries[0].Item, !entries[0].Included);
        }

        var includeAll = entries.Any(e => !e.Included);
        var items = entries.Select(e => e.Item).ToList();
        return Edit(includeAll ? $"Include {items.Count} entries" : $"Exclude {items.Count} entries",
            c => CollectionOperations.SetIncluded(c, items, includeAll));
    }

    /// <summary>Moves an entry to an explicit collection index when no visual filter is active.</summary>
    public bool MoveEntryTo(EntryViewModel entry, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsFiltered)
        {
            _project.Status = "Clear search and filters before dragging to change collection order.";
            return false;
        }

        var from = Entries.IndexOf(entry);
        if (from < 0)
        {
            return false;
        }

        var destination = from < targetIndex ? targetIndex - 1 : targetIndex;
        destination = Math.Clamp(destination, 0, Entries.Count - 1);
        if (destination == from)
        {
            return false;
        }

        return Edit("Move entry", c => CollectionOperations.Move(c, entry.Item, destination));
    }

    [RelayCommand]
    private void SetFilterMode(CollectionFilterMode mode) => FilterMode = mode;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        FilterMode = CollectionFilterMode.All;
        ClearDetailFilters();
    }

    [RelayCommand]
    private void ClearDetailFilters()
    {
        if (!HasDetailFilters)
        {
            return;
        }

        _openingFilter = _sourceFilter = _formatFilter = _genreFilter = _studioFilter = _ratingFilter = FilterOption.AnyValue;
        OnDetailFiltersChanged();
    }

    [RelayCommand]
    private void ToggleDetailFilters() => ShowDetailFilters = !ShowDetailFilters;

    [RelayCommand]
    private void SetViewMode(CollectionViewMode mode) => ViewMode = mode;

    [RelayCommand]
    private void SetDensity(CardDensity next)
    {
        if (IsCompactViewport && next == EffectiveDensity)
        {
            return;
        }

        Density = next;
    }

    partial void OnCompactViewportLevelChanged(int value)
    {
        foreach (var entry in Entries)
        {
            entry.RefreshThumbnailResolution();
        }
    }

    partial void OnViewModeChanged(CollectionViewMode value)
    {
        if (!_initialized)
        {
            return;
        }

        _project.App.Preferences.Update(s => s with { CollectionViewMode = value });
        foreach (var entry in Entries)
        {
            entry.RefreshThumbnailResolution();
        }
    }

    partial void OnDensityChanged(CardDensity value)
    {
        if (!_initialized)
        {
            return;
        }

        _project.App.Preferences.Update(s => s with { CollectionDensity = value });
        foreach (var entry in Entries)
        {
            entry.RefreshThumbnailResolution();
        }
    }

    [RelayCommand]
    private void ToggleInspector()
    {
        InspectorOpen = !InspectorOpen;
        _project.App.Preferences.Update(s => s with { CollectionInspectorOpen = InspectorOpen });
    }

    [RelayCommand]
    private void CloseInspector()
    {
        if (InspectorOpen)
        {
            InspectorOpen = false;
            _project.App.Preferences.Update(s => s with { CollectionInspectorOpen = false });
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void BeginInlineRename()
    {
        RenameText = Name;
        IsRenamingInline = true;
    }

    [RelayCommand]
    private void CommitInlineRename()
    {
        IsRenamingInline = false;
        Rename();
    }

    [RelayCommand]
    private void CancelInlineRename()
    {
        RenameText = Name;
        IsRenamingInline = false;
    }

    /// <summary>Opens a themed prompt to name and create a new board from all included shows.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PromptCreateBoardAsync()
    {
        var suggestion = string.IsNullOrWhiteSpace(NewBoardName) ? $"{Name} ranking" : NewBoardName;
        var countLabel = IncludedCount == 1 ? "1 show" : $"{IncludedCount} shows";
        var note = IncludedCount == 0
            ? "0 shows are currently included. You can still create an empty board and update it later, or include shows first."
            : $"Includes {countLabel} in effective collection order (search and filter chips never narrow board membership).";
        var chosen = _project.App.Dialogs?.PromptText(
            "Create board from included shows",
            $"Create a new ranking board from '{Name}'.",
            "Board _name",
            suggestion,
            "Create board",
            note);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return;
        }

        NewBoardName = chosen;
        await CreateBoardAsync();
    }

    [RelayCommand]
    private void DismissCoverStatus()
    {
        _showingDamageSummary = false;
        CoverStatus = string.Empty;
    }

    [RelayCommand]
    private void DismissRefreshStatus() => RefreshStatus = string.Empty;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void IncludeAll() => Edit("Include all", c => CollectionOperations.SetAllIncluded(c, true));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ExcludeAll() => Edit("Exclude all", c => CollectionOperations.SetAllIncluded(c, false));

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void IncludeVisible()
    {
        var items = View.Cast<EntryViewModel>().Select(e => e.Item).ToList();
        if (items.Count > 0)
        {
            Edit("Include shown entries", c => CollectionOperations.SetIncluded(c, items, true));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ExcludeVisible()
    {
        var items = View.Cast<EntryViewModel>().Select(e => e.Item).ToList();
        if (items.Count > 0)
        {
            Edit("Exclude shown entries", c => CollectionOperations.SetIncluded(c, items, false));
        }
    }

    private bool CanEdit() => !_project.EditsSuspended;

    /// <summary>
    /// Fetches LiveChart's details and videos for every show in the collection that lacks them, one show at a time at
    /// LiveChart's pace. Each show is saved as it arrives, so stopping keeps what was fetched and a later run resumes.
    /// </summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanEdit))]
    private async Task FetchDetailsAsync(CancellationToken cancellationToken)
    {
        var state = _project.Session.Current;
        if (!state.Collections.TryGetValue(Id, out var collection))
        {
            return;
        }

        var items = collection.Entries.Select(e => e.Item).ToList();
        var missing = items.Count(i => AnimeDetailsAcquisition.NeedsDetails(state, i));
        if (missing == 0)
        {
            DetailsStatus = $"Details and videos for all {items.Count} shows are already saved in this project.";
            return;
        }

        if (_project.IsFetchingDetails)
        {
            DetailsStatus = "Another collection is fetching details. Wait for it to finish or stop it, then fetch details here.";
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _project.Lifetime);
        _project.IsFetchingDetails = true;
        IsFetchingDetails = true;
        DetailsProgressCompleted = 0;
        DetailsProgressTotal = missing;
        DetailsStatus = $"Fetching details and videos for {missing} shows from LiveChart, one page every 5 seconds (about {EstimateDuration(missing)}). You can keep working meanwhile.";
        try
        {
            var progress = new Progress<DetailsRunReport>(ShowDetailsReport);
            var report = await AnimeDetailsAcquisition.AcquireAsync(_project.Session, items, _project.App.AnimeDetails, RetryBudget.CreateDefault(), progress, linked.Token);
            DetailsProgressCompleted = report.Done;
            DetailsStatus = Describe(report) + (report.Outcome == DetailsRunOutcome.Unreachable ? " " + await NetworkHint.DescribeUnreachableAsync() : string.Empty);
        }
        finally
        {
            IsFetchingDetails = false;
            _project.IsFetchingDetails = false;
        }
    }

    [RelayCommand]
    private void DismissDetailsStatus() => DetailsStatus = string.Empty;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ToggleSelected()
    {
        if (SelectedEntry is { } entry)
        {
            SetIncluded(entry.Item, !entry.Included);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private void MoveUp() => MoveSelected(index => index - 1);

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private void MoveDown() => MoveSelected(index => index + 1);

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private void MoveToTop() => MoveSelected(_ => 0);

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private void MoveToBottom() => MoveSelected(_ => Entries.Count - 1);

    private bool HasSelection() => SelectedEntry is not null && !_project.EditsSuspended;

    private bool CanMoveSelected() => SelectedEntry is not null && !IsFiltered && !_project.EditsSuspended;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Rename()
    {
        if (!string.IsNullOrWhiteSpace(RenameText) && RenameText.Trim() != Name)
        {
            Edit("Rename collection", c => CollectionOperations.Rename(c, RenameText));
        }
    }

    [RelayCommand(CanExecute = nameof(CanSetTitleOverride))]
    private void SetTitleOverride()
    {
        if (SelectedEntry is { } entry)
        {
            Edit("Set custom title", c => CollectionOperations.SetTitleOverride(c, entry.Item, TitleOverrideText));
        }
    }

    private bool CanSetTitleOverride() => SelectedEntry is not null && !string.IsNullOrWhiteSpace(TitleOverrideText) && !_project.EditsSuspended;

    [RelayCommand(CanExecute = nameof(CanClearTitleOverride))]
    private void ClearTitleOverride()
    {
        if (SelectedEntry is { } entry)
        {
            Edit("Clear custom title", c => CollectionOperations.SetTitleOverride(c, entry.Item, null));
        }
    }

    private bool CanClearTitleOverride() => SelectedEntry is { HasCustomTitle: true } && !_project.EditsSuspended;

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanEdit))]
    private async Task FetchCoversAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _project.Lifetime);
        _showingDamageSummary = false;
        IsFetchingCovers = true;
        CoverProgressCompleted = 0;
        CoverProgressTotal = Math.Max(1, Entries.Count);
        CoverStatus = "Fetching covers from LiveChart (at most two at a time)...";
        try
        {
            var progress = new Progress<CoverRunReport>(ShowCoverReport);
            var report = await CoverAcquisition.AcquireAsync(_project.Session, Id, Services.Covers, Services.Assets, RetryBudget.CreateDefault(), progress, linked.Token);
            ShowCoverReport(report);
            CoverStatus = Describe(report) + (report.Unreachable > 0 ? " " + await NetworkHint.DescribeUnreachableAsync() : string.Empty);

            // A run may have repaired files in place; re-verify every selected cover file without dropping
            // known-broken verdicts before verification confirms each file is intact on disk.
            _checkedAssets.Clear();
            foreach (var entry in Entries)
            {
                entry.ResetThumbnailFailure();
            }

            Refresh(_project.Session.Current);
            await InspectSelectedCoverAsync();
        }
        finally
        {
            IsFetchingCovers = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanEdit))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        var state = _project.Session.Current;
        var collection = state.Collection(Id);
        IsCheckingUpdates = true;
        RefreshStatus = "Fetching the latest LiveChart order...";
        CatalogueOutcome outcome;
        try
        {
            outcome = await CatalogueAcquisition.AcquireAsync(_project.App.Catalogue, _project.App.Staging, collection.Context, RetryBudget.CreateDefault(), null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RefreshStatus = "Update check cancelled. Nothing changed.";
            return;
        }
        finally
        {
            IsCheckingUpdates = false;
        }

        if (outcome is CatalogueOutcome.Incomplete incomplete)
        {
            RefreshStatus = incomplete.Failure == SeasonPageFailure.Unreachable
                ? $"Nothing changed. {await NetworkHint.DescribeUnreachableAsync()}"
                : $"Nothing changed: {incomplete.FailedSeason.Season} {incomplete.FailedSeason.Year} could not be read ({AcquisitionViewModel.Explain(incomplete.Failure)}). Check again to resume.";
            return;
        }

        var snapshot = ((CatalogueOutcome.Complete)outcome).Snapshot;
        state = _project.Session.Current;
        if (!state.Collections.TryGetValue(Id, out collection))
        {
            RefreshStatus = string.Empty;
            return;
        }

        var preview = CollectionOperations.PreviewRefresh(collection, snapshot, state.Observation);
        if (preview.IsEmpty)
        {
            RefreshStatus = "Already up to date with LiveChart.";
            return;
        }

        var review = new RefreshReviewViewModel(collection, snapshot, preview, state);
        if (_project.App.Dialogs?.ShowRefreshReview(review) != true)
        {
            RefreshStatus = "Update not applied. Nothing changed.";
            return;
        }

        var result = _project.Execute(review.AdoptSourceOrder ? "Apply update with LiveChart order" : "Apply update",
            s => s.WithSnapshot(snapshot).WithCollection(CollectionOperations.ApplyRefresh(s.Collection(Id), snapshot, review.AdoptSourceOrder)));
        RefreshStatus = result is SessionResult.Accepted
            ? $"Update applied: {preview.Added.Length} new (excluded until you include them), {preview.NewlyMissing.Length} no longer listed." +
              (preview.Added.IsEmpty && preview.CoverCandidateChanges.IsEmpty ? string.Empty : " Fetch covers to download covers for new shows and newly listed images.")
            : "Update not applied.";
    }

    [RelayCommand(CanExecute = nameof(CanMarkCoverMissing))]
    private async Task MarkCoverMissingAsync()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        var title = entry.Title;
        if (_project.App.Dialogs?.Confirm("Mark cover missing",
                $"Clear the damaged cover of '{title}'? The entry keeps its title, position, and inclusion, and shows no cover until you fetch covers again. Undo restores the current reference.") != true)
        {
            return;
        }

        var result = await CoverRecovery.MarkMissingAsync(_project.Session, Services.Assets, Id, entry.Item, _project.Lifetime);
        FinishRecovery(entry.Item, result, $"The cover of '{title}' is marked missing.");
    }

    private bool CanMarkCoverMissing() => SelectedCoverBroken && !_project.EditsSuspended;

    [RelayCommand(CanExecute = nameof(CanUseNewCover))]
    private async Task UseNewCoverAsync()
    {
        if (SelectedEntry is not { } entry || _offer is not { } offer)
        {
            return;
        }

        var result = await CoverRecovery.ReplaceAsync(_project.Session, Services.Assets, Id, entry.Item, offer, _project.Lifetime);
        FinishRecovery(entry.Item, result, $"'{entry.Title}' now uses the new cover from LiveChart.");
    }

    private bool CanUseNewCover() => SelectedCoverBroken && OfferThumbnail is not null && !_project.EditsSuspended;

    /// <summary>Creates a board with every included show unranked in collection order, pinning titles and intact covers.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateBoard))]
    private async Task CreateBoardAsync()
    {
        var state = _project.Session.Current;
        if (!state.Collections.TryGetValue(Id, out var collection))
        {
            return;
        }

        IReadOnlySet<AssetHash> intact;
        try
        {
            intact = await AssetChecks.IntactAsync(Services.Assets, collection.IncludedEntries.Where(e => e.Cover is not null).Select(e => state.Assets[e.Cover!.Asset]), _project.Lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_project.Lifetime.IsCancellationRequested)
        {
            return;
        }

        var id = BoardId.New();
        var name = NewBoardName.Trim();
        var result = _project.Execute($"Create board '{name}'", s =>
        {
            var current = s.Collection(Id);
            return s.WithBoard(BoardOperations.Create(id, name, current, e => s.ResolveTitle(current, e), intact.Contains, BoardOperations.DefaultTiers()));
        });
        if (result is SessionResult.Accepted)
        {
            NewBoardName = string.Empty;
            _project.ShowBoard(id);
        }
    }

    private bool CanCreateBoard() => !string.IsNullOrWhiteSpace(NewBoardName) && !_project.EditsSuspended;

    /// <summary>
    /// Exports the included shows' covers from the saved revision, in effective order (search never applies), into
    /// a new folder created inside the chosen location.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ExportCoversAsync()
    {
        var count = _project.Session.Current.Collections.TryGetValue(Id, out var shown) ? shown.IncludedEntries.Count() : 0;
        if (_project.App.Dialogs?.ChooseFolder($"Choose where to create the cover folder for '{Name}' ({count} included shows in collection order)") is not { } parent)
        {
            return;
        }

        if (await _project.SavedRevisionAsync() is not { } saved || !saved.Collections.TryGetValue(Id, out var collection))
        {
            return;
        }

        var baseName = (ProjectFolderName.From(collection.Name) ?? "Covers") + " covers";
        var destination = Path.Combine(parent, baseName);
        for (var n = 2; Directory.Exists(destination) || File.Exists(destination); n++)
        {
            destination = Path.Combine(parent, $"{baseName} ({n})");
        }

        try
        {
            var manifest = await SelectedCoverExporter.ExportAsync(collection, e => saved.ResolveTitle(collection, e).Text, saved.Assets, Services.Assets, destination, _project.Lifetime);
            _project.Status = $"Exported {manifest.Entries.Length} covers to {destination}.";
        }
        catch (ExportException ex)
        {
            _project.App.Dialogs?.Inform("Covers not exported", ex.Message, ex.Affected.IsEmpty ? null : ex.Affected);
            _project.Status = "Covers not exported: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Delete()
    {
        var state = _project.Session.Current;
        if (!state.Collections.TryGetValue(Id, out var collection))
        {
            return;
        }

        var boards = state.Boards.Values.Where(b => b.Collection == Id).Select(b => b.Name).Order(StringComparer.CurrentCulture).ToList();
        if (boards.Count > 0)
        {
            _project.Status = $"'{collection.Name}' cannot be deleted while these boards use it: {string.Join(", ", boards)}. Delete those boards first.";
            return;
        }

        if (_project.App.Dialogs?.Confirm("Delete collection",
                $"Delete the collection '{collection.Name}' ({collection.Entries.Length} shows)? Downloaded covers stay in the project. You can undo this until the project is closed.") != true)
        {
            return;
        }

        if (_project.Execute($"Delete collection '{collection.Name}'", s => s.WithoutCollection(Id)) is SessionResult.Accepted)
        {
            _project.Status = $"Deleted '{collection.Name}'. Use Undo to restore it.";
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        UpdateVisibleCount();
        FilterOrSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFilterModeChanged(CollectionFilterMode value)
    {
        View.Refresh();
        UpdateVisibleCount();
        FilterOrSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedEntryChanged(EntryViewModel? value)
    {
        var state = _project.Session.Current;
        _loadedOverride = value is not null && state.Collections.TryGetValue(Id, out var c) && c.IndexOf(value.Item) >= 0
            ? c.Entry(value.Item).TitleOverride
            : null;
        TitleOverrideText = _loadedOverride ?? string.Empty;
        UpdateReferencingBoards(state);
        _ = InspectSelectedCoverAsync();
    }

    private void UpdateReferencingBoards(ProjectState state)
    {
        if (SelectedEntry is not { } entry)
        {
            SelectedReferencingBoards = "None";
            return;
        }

        var names = state.Boards.Values
            .Where(b => b.Contains(entry.Item))
            .Select(b => b.Name)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SelectedReferencingBoards = names.Count == 0 ? "Not on any board yet" : string.Join(", ", names);
    }

    private void UpdateVisibleCount()
    {
        VisibleCount = IsFiltered ? View.Cast<object>().Count() : Entries.Count;
    }

    private void NotifyMutationCommands()
    {
        BeginInlineRenameCommand.NotifyCanExecuteChanged();
        PromptCreateBoardCommand.NotifyCanExecuteChanged();
        IncludeAllCommand.NotifyCanExecuteChanged();
        ExcludeAllCommand.NotifyCanExecuteChanged();
        IncludeVisibleCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        FetchCoversCommand.NotifyCanExecuteChanged();
        FetchDetailsCommand.NotifyCanExecuteChanged();
        ExcludeVisibleCommand.NotifyCanExecuteChanged();
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        ExportCoversCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        MarkCoverMissingCommand.NotifyCanExecuteChanged();
        ToggleSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveToTopCommand.NotifyCanExecuteChanged();
        MoveToBottomCommand.NotifyCanExecuteChanged();
        SetTitleOverrideCommand.NotifyCanExecuteChanged();
        ClearTitleOverrideCommand.NotifyCanExecuteChanged();
        CreateBoardCommand.NotifyCanExecuteChanged();
        UseNewCoverCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanReorder));
    }

    /// <summary>
    /// Checks the selected entry's cover file and offers the recovery actions that apply. Superseded checks are
    /// ignored. The task handles its own failures, leaving the actions disabled.
    /// </summary>
    private async Task InspectSelectedCoverAsync()
    {
        var version = ++_inspection;
        var entry = SelectedEntry;
        var state = _project.Session.Current;
        _inspectedCover = entry is not null && state.Collections.TryGetValue(Id, out var collection) && collection.IndexOf(entry.Item) >= 0
            ? collection.Entry(entry.Item).Cover
            : null;
        if (entry is null)
        {
            ShowCoverCondition(null, null, string.Empty);
            return;
        }

        try
        {
            var condition = await CoverRecovery.InspectAsync(state, Id, entry.Item, Services.Assets, _project.Lifetime);
            if (version != _inspection)
            {
                return;
            }

            var status = _coverStatuses.GetValueOrDefault(entry.Item);
            var offer = condition == CoverCondition.Broken && status?.Replacement is { } r && r.Provenance.Occurrence == _inspectedCover?.Occurrence ? r : null;
            var thumbnail = offer is null ? null : await Services.Thumbnails.LoadAsync(offer.Asset, ThumbnailLoader.MediumPixelHeight, _project.Lifetime);
            if (version != _inspection)
            {
                return;
            }

            if (condition == CoverCondition.Broken && _inspectedCover is { } brokenCover)
            {
                if (_brokenAssets.Add(brokenCover.Asset))
                {
                    _showingDamageSummary = true;
                    Refresh(_project.Session.Current);
                }
            }
            else if (condition == CoverCondition.Valid && _inspectedCover is { } validCover)
            {
                var removedBroken = _brokenAssets.Remove(validCover.Asset);
                var clearedUnavailable = false;
                if (state.Collections.TryGetValue(Id, out var currentCol))
                {
                    foreach (var candidate in Entries.Where(e => e.CoverUnavailable))
                    {
                        if (currentCol.IndexOf(candidate.Item) >= 0 && currentCol.Entry(candidate.Item).Cover?.Asset == validCover.Asset)
                        {
                            candidate.ResetThumbnailFailure();
                            clearedUnavailable = true;
                        }
                    }
                }

                // The selected entry, when unavailable, is one of the candidates above. Resetting it again would start a
                // second load of the same thumbnail, which races the first on the cache file and can mark it unavailable.
                if (removedBroken || clearedUnavailable)
                {
                    Refresh(_project.Session.Current);
                }
            }

            ShowCoverCondition(condition, thumbnail is null ? null : offer, condition switch
            {
                CoverCondition.Valid => "Cover: saved and intact.",
                CoverCondition.None when status?.Status == EntryCoverStatus.Missing => "Cover: LiveChart has no usable cover for this show.",
                CoverCondition.None => "Cover: none. Fetch covers to download it.",
                _ when thumbnail is not null => "Cover: the saved file is missing or damaged, and LiveChart now supplies the different cover shown here. Use it, or mark the cover missing.",
                _ => "Cover: the saved file is missing or damaged. Fetch covers to restore the exact original, or mark the cover missing (works offline).",
            }, thumbnail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException or InvalidOperationException or FormatException or COMException or ArgumentException)
        {
            if (version == _inspection)
            {
                ShowCoverCondition(null, null, "Cover: the file could not be checked (" + ex.Message + ").");
            }
        }
    }

    /// <summary>
    /// Checks selected cover files against their recorded size and hash in the background, so damaged or missing
    /// files are shown as such without a network request. The task handles its own failures; an unreadable file is
    /// left unmarked and is reported when the entry is selected.
    /// </summary>
    private async Task CheckCoverFilesAsync(List<AssetRecord> assets)
    {
        _scanning = true;
        var addedDamage = false;
        try
        {
            foreach (var asset in assets)
            {
                AssetAvailability availability;
                try
                {
                    availability = await Services.Assets.CheckAsync(asset, _project.Lifetime);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _checkedAssets.Add(asset.Hash);
                    continue;
                }

                _checkedAssets.Add(asset.Hash);
                if (availability == AssetAvailability.Available)
                {
                    _brokenAssets.Remove(asset.Hash);
                }
                else if (_brokenAssets.Add(asset.Hash))
                {
                    addedDamage = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _scanning = false;
        }

        _showingDamageSummary |= addedDamage;

        // Files that became checkable meanwhile (new covers) are picked up by this refresh.
        Refresh(_project.Session.Current);
    }

    private void ShowCoverCondition(CoverCondition? condition, CoverReplacement? offer, string text, ImageSource? thumbnail = null)
    {
        _offer = offer;
        OfferThumbnail = thumbnail;
        SelectedCoverBroken = condition == CoverCondition.Broken;
        SelectedCoverText = text;
    }

    private void FinishRecovery(LiveChartId item, SessionResult result, string success)
    {
        if (result is SessionResult.Accepted)
        {
            _coverStatuses.Remove(item);
            _project.Status = success;
            Refresh(_project.Session.Current);
        }
        else if (result is SessionResult.Rejected rejected)
        {
            _project.Status = rejected.Reason;
        }
        else if (result is SessionResult.Suspended)
        {
            _project.Status = "Changes are suspended because saving failed. Retry the save or discard unsaved changes.";
        }

        _ = InspectSelectedCoverAsync();
    }

    partial void OnLanguageChanged(LanguageOption value)
    {
        if (!_updating)
        {
            if (!Edit("Change title language", c => CollectionOperations.SetLanguage(c, value.Value))
                && _project.Session.Current.Collections.TryGetValue(Id, out var collection))
            {
                _updating = true;
                try
                {
                    Language = LanguageOptions.First(l => l.Value == collection.Language);
                }
                finally
                {
                    _updating = false;
                }
            }
        }
    }

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(RenameText) || _updating)
        {
            RenameText = value;
        }
    }

    private void MoveSelected(Func<int, int> target)
    {
        if (SelectedEntry is not { } entry || IsFiltered)
        {
            return;
        }

        var index = Entries.IndexOf(entry);
        var destination = Math.Clamp(target(index), 0, Entries.Count - 1);
        if (destination != index && Edit("Move entry", c => CollectionOperations.Move(c, entry.Item, destination)))
        {
            EntryMoved?.Invoke(this, entry);
        }
    }

    private bool Edit(string label, Func<CollectionState, CollectionState> edit)
    {
        if (_project.Execute(label, s => s.WithCollection(edit(s.Collection(Id)))) is not SessionResult.Accepted)
        {
            return false;
        }

        Refresh(_project.Session.Current);
        return true;
    }

    private bool Matches(object item)
    {
        if (item is not EntryViewModel entry)
        {
            return true;
        }

        if (HasDetailFilters && !MatchesDetailFilters(entry.Facts))
        {
            return false;
        }

        var chipOk = FilterMode switch
        {
            CollectionFilterMode.Included => entry.Included,
            CollectionFilterMode.Excluded => !entry.Included,
            CollectionFilterMode.NoCover => !entry.HasCover,
            CollectionFilterMode.NeedsAttention => entry.NeedsAttention,
            _ => true,
        };
        if (!chipOk)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return entry.SearchText.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    private bool MatchesDetailFilters(AnimeFacts facts)
    {
        var opening = _openingFilter switch
        {
            "yes" => facts.HasOpening == true,
            "no" => facts.HasOpening == false,
            FilterOption.UnknownValue => facts.HasOpening is null,
            _ => true,
        };
        var rating = _ratingFilter switch
        {
            FilterOption.AnyValue => true,
            FilterOption.UnknownValue => facts.Rating is null,
            var minimum => facts.Rating is { } r && double.TryParse(minimum, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var min) && r >= min,
        };
        return opening
            && rating
            && MatchesValue(_sourceFilter, facts.SourceMaterial)
            && MatchesValue(_formatFilter, facts.Format)
            && (_genreFilter == FilterOption.AnyValue || facts.Genres.Contains(_genreFilter, StringComparer.OrdinalIgnoreCase))
            && (_studioFilter == FilterOption.AnyValue || facts.Studios.Contains(_studioFilter, StringComparer.OrdinalIgnoreCase));

        static bool MatchesValue(string filter, string? value) => filter switch
        {
            FilterOption.AnyValue => true,
            FilterOption.UnknownValue => value is null,
            _ => string.Equals(value, filter, StringComparison.OrdinalIgnoreCase),
        };
    }

    private void SetDetailFilter(ref string field, string? value, [CallerMemberName] string? name = null)
    {
        // A combo box clears its selection while its options are replaced; that is not a user choice.
        if (_rebuildingFilterOptions || value is null || field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(name);
        OnDetailFiltersChanged();
    }

    private void OnDetailFiltersChanged()
    {
        NotifyDetailFilterValues();
        OnPropertyChanged(nameof(ActiveDetailFilterCount));
        OnPropertyChanged(nameof(HasDetailFilters));
        OnPropertyChanged(nameof(DetailFiltersLabel));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(FilterSummary));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveToTopCommand.NotifyCanExecuteChanged();
        MoveToBottomCommand.NotifyCanExecuteChanged();
        View.Refresh();
        UpdateVisibleCount();
        FilterOrSearchChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyDetailFilterValues()
    {
        OnPropertyChanged(nameof(OpeningFilter));
        OnPropertyChanged(nameof(SourceFilter));
        OnPropertyChanged(nameof(FormatFilter));
        OnPropertyChanged(nameof(GenreFilter));
        OnPropertyChanged(nameof(StudioFilter));
        OnPropertyChanged(nameof(RatingFilter));
    }

    partial void OnMissingDetailsCountChanged(int value)
    {
        OnPropertyChanged(nameof(FetchDetailsToolTip));
        OnPropertyChanged(nameof(FilterSummary));
    }

    /// <summary>
    /// Rebuilds the details filter lists from the shown facts, with a show count per choice. A chosen value that no
    /// show has any more falls back to no filter.
    /// </summary>
    private void RebuildDetailFilterOptions()
    {
        var facts = Entries.Select(e => e.Facts).ToList();
        var opening = new List<FilterOption>
        {
            new(FilterOption.AnyValue, "Any OP status"),
            new("yes", $"Has OP ({facts.Count(f => f.HasOpening == true)})"),
            new("no", $"No OP ({facts.Count(f => f.HasOpening == false)})"),
            new(FilterOption.UnknownValue, $"Not checked ({facts.Count(f => f.HasOpening is null)})"),
        };
        var rating = new List<FilterOption> { new(FilterOption.AnyValue, "Any rating") };
        foreach (var minimum in new[] { 6.0, 7.0, 7.5, 8.0, 8.5 })
        {
            rating.Add(new(minimum.ToString("0.0", CultureInfo.InvariantCulture), $"{minimum.ToString("0.0", CultureInfo.CurrentCulture)}+ ({facts.Count(f => f.Rating >= minimum)})"));
        }

        rating.Add(new(FilterOption.UnknownValue, $"Unrated ({facts.Count(f => f.Rating is null)})"));

        _rebuildingFilterOptions = true;
        try
        {
            OpeningOptions = Same(OpeningOptions, opening);
            RatingOptions = Same(RatingOptions, rating);
            SourceOptions = Same(SourceOptions, ValueOptions("All sources", facts.Select(f => f.SourceMaterial), _sourceFilter));
            FormatOptions = Same(FormatOptions, ValueOptions("All formats", facts.Select(f => f.Format), _formatFilter));
            GenreOptions = Same(GenreOptions, ValueOptions("All genres", facts.SelectMany(f => f.Genres), _genreFilter, includeUnknown: false));
            StudioOptions = Same(StudioOptions, ValueOptions("All studios", facts.SelectMany(f => f.Studios), _studioFilter, includeUnknown: false));
        }
        finally
        {
            _rebuildingFilterOptions = false;
        }

        // Re-select the current values in combo boxes whose options were just replaced.
        NotifyDetailFilterValues();

        static IReadOnlyList<FilterOption> Same(IReadOnlyList<FilterOption> current, List<FilterOption> next) =>
            current.SequenceEqual(next) ? current : next;
    }

    /// <summary>
    /// Lists each value with its show count. The selected value stays listed, with a count of zero, after no show has
    /// it any more, so new details never silently widen an active filter.
    /// </summary>
    private static List<FilterOption> ValueOptions(string anyLabel, IEnumerable<string?> values, string selected, bool includeUnknown = true)
    {
        var list = values.ToList();
        var options = new List<FilterOption> { new(FilterOption.AnyValue, anyLabel) };
        options.AddRange(list
            .Where(v => v is not null)
            .GroupBy(v => v!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                // Keep the selected spelling, so a casing change in fetched details never blanks the bound combo box.
                var value = string.Equals(g.Key, selected, StringComparison.OrdinalIgnoreCase) ? selected : g.Key;
                return new FilterOption(value, $"{value} ({g.Count()})");
            }));
        var unknown = list.Count(v => v is null);
        if (includeUnknown && unknown > 0)
        {
            options.Add(new(FilterOption.UnknownValue, $"Unknown ({unknown})"));
        }

        if (!options.Any(o => string.Equals(o.Value, selected, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new(selected, selected == FilterOption.UnknownValue ? "Unknown (0)" : $"{selected} (0)"));
        }

        return options;
    }

    /// <summary>Two catalogue pages per show (videos and profile) at five seconds each.</summary>
    private static string EstimateDuration(int shows)
    {
        var minutes = (int)Math.Ceiling(shows * 10 / 60.0);
        return minutes <= 1 ? "a minute" : $"{minutes} minutes";
    }

    private void ShowDetailsReport(DetailsRunReport report)
    {
        if (!IsFetchingDetails)
        {
            return;
        }

        DetailsProgressTotal = Math.Max(1, report.Total);
        DetailsProgressCompleted = report.Done;
        var remaining = report.Total - report.Done;
        DetailsStatus = $"Details: {report.Done} of {report.Total} shows checked" +
            (report.Failed > 0 ? $", {report.Failed} failed" : string.Empty) +
            (remaining > 0 ? $". About {EstimateDuration(remaining)} left." : ".");
    }

    private static string Describe(DetailsRunReport report)
    {
        var failures = report.Failed > 0
            ? $" {report.Failed} failed; fetch details again to retry them." + (report.LastFailure is { } last ? $" Last error: {last}" : string.Empty)
            : string.Empty;
        return report.Outcome switch
        {
            DetailsRunOutcome.Cancelled => $"Stopped after {report.Done} of {report.Total} shows. Fetched details are saved; fetch details again to continue.",
            DetailsRunOutcome.Paused => $"LiveChart asked the app to wait, so fetching paused after {report.Done} of {report.Total} shows. Fetch details again later to resume.",
            DetailsRunOutcome.Unreachable => $"LiveChart could not be reached after {report.Done} of {report.Total} shows.",
            DetailsRunOutcome.NotSaved => "Fetching stopped because the project could not be saved. Retry the save, then fetch details again.",
            _ => $"Details and videos fetched for {report.Fetched} of {report.Total} shows." + failures,
        };
    }

    private void ShowCoverReport(CoverRunReport report)
    {
        foreach (var status in report.Entries)
        {
            _coverStatuses[status.Item] = status;
        }

        CoverProgressTotal = Math.Max(1, report.Total);
        CoverProgressCompleted = report.Completed + report.Cached + report.Missing + report.Failed;
        CoverStatus = $"Covers: {report.Completed + report.Cached} of {report.Total} ready" +
            (report.Pending > 0 ? $", {report.Pending} pending" : string.Empty) +
            (report.Failed + report.Missing > 0 ? $", {report.Failed + report.Missing} without a cover" : string.Empty);
        Refresh(_project.Session.Current);
    }

    private static string Describe(CoverRunReport report)
    {
        var parts = new List<string> { $"{report.Completed} downloaded", $"{report.Cached} already available" };
        if (report.Missing > 0)
        {
            parts.Add($"{report.Missing} with no usable LiveChart image");
        }

        if (report.Failed > 0)
        {
            parts.Add($"{report.Failed} failed (fetch again to retry)");
        }

        if (report.Paused > 0)
        {
            parts.Add($"{report.Paused} paused (fetch again to resume)");
        }

        if (report.Cancelled > 0)
        {
            parts.Add($"{report.Cancelled} cancelled");
        }

        return $"Covers for {report.Total} shows: {string.Join(", ", parts)}.";
    }
}
