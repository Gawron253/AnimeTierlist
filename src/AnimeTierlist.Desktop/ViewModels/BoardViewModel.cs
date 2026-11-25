using System.Collections.ObjectModel;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.Services;
using AnimeTierlist.Infrastructure.Exports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

public sealed record TierColorOption(string Label, TierColor Value)
{
    /// <summary>Screen readers and UI Automation announce the label, not the record shape.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// One ranking board: tiers with ordered placements and the seed-ordered unranked pool. Every move, tier edit, rename,
/// deletion, and reviewed update is one undoable project command. Keyboard commands mirror drag and drop.
/// </summary>
public sealed partial class BoardViewModel : ObservableObject
{
    private readonly ProjectViewModel _project;
    private readonly Dictionary<BoardEntryKey, BoardEntryViewModel> _entries = [];
    private readonly Dictionary<TierId, TierViewModel> _tiers = [];
    private readonly HashSet<AssetHash> _checked = [];
    private readonly HashSet<AssetHash> _broken = [];
    private bool _checking;
    private int _checkGeneration;
    private string? _loadedName;
    private (TierId Id, string Name, TierColor Color)? _loadedTier;
    private int _searchMatchCursor = -1;
    private bool _initialized;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceCollectionButtonLabel))]
    private string _sourceCollectionName = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _rankedCount;

    [ObservableProperty]
    private int _unrankedCount;

    [ObservableProperty]
    private double _rankedPercent;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private bool _isRenamingInline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBoardSearch))]
    private string _boardSearchText = string.Empty;

    [ObservableProperty]
    private string _boardSearchMatchSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBoardSearchMatches))]
    [NotifyCanExecuteChangedFor(nameof(NextSearchMatchCommand), nameof(PreviousSearchMatchCommand))]
    private int _boardSearchMatchCount;

    [ObservableProperty]
    private string _dragStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveDensity), nameof(CardWidth), nameof(TileWidth), nameof(CardCoverWidth), nameof(CardCoverHeight), nameof(TileHeight), nameof(DesiredThumbnailHeight))]
    private CardDensity _density;

    /// <summary>0 = normal viewport, 1 = compact viewport (&lt;390 DIP), 2 = ultra-compact viewport (&lt;310 DIP, e.g. save-failed at 960x540).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompactViewport), nameof(IsNotCompactViewport), nameof(EffectiveDensity), nameof(CardWidth), nameof(TileWidth), nameof(CardCoverWidth), nameof(CardCoverHeight), nameof(CardTitleMaxHeight), nameof(RowMinHeight), nameof(TileHeight), nameof(DesiredThumbnailHeight))]
    private int _compactViewportLevel;

    [ObservableProperty]
    private bool _trayCollapsed;

    [ObservableProperty]
    private double _trayHeight = 184;

    [ObservableProperty]
    private bool _inspectorOpen;

    [ObservableProperty]
    private int _inspectorTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    [NotifyCanExecuteChangedFor(nameof(MoveToTierCommand), nameof(MoveToUnrankedCommand), nameof(MoveLeftCommand), nameof(MoveRightCommand), nameof(MoveToTierAboveCommand), nameof(MoveToTierBelowCommand), nameof(MoveSelectedToTierCommand), nameof(OpenSelectedEntryDetailsCommand))]
    private BoardEntryViewModel? _selectedEntry;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditTierCommand), nameof(MoveTierUpCommand), nameof(MoveTierDownCommand), nameof(DeleteTierCommand))]
    private TierViewModel? _selectedTier;

    [ObservableProperty]
    private string _tierNameText = string.Empty;

    [ObservableProperty]
    private bool _includeUnrankedInPng = true;

    [ObservableProperty]
    private TierColorOption _tierColor;

    /// <summary>Creates a board editor view model bound to a persisted board ID.</summary>
    public BoardViewModel(ProjectViewModel project, BoardId id)
    {
        _project = project;
        Id = id;
        Unranked = new TierViewModel(this, null, "Unranked");
        _tierColor = ColorOptions[0];
        _density = project.App.Preferences.Current.BoardDensity;
        _trayCollapsed = project.App.Preferences.Current.BoardTrayCollapsed;
        _trayHeight = project.App.Preferences.Current.BoardTrayHeight;
        _inspectorOpen = project.App.Preferences.Current.BoardInspectorOpen;
        _initialized = true;
    }

    /// <summary>Raised after a keyboard or drag move so the view can keep focus on the moved show.</summary>
    public event EventHandler<BoardEntryViewModel>? SelectionMoved;

    public BoardId Id { get; }

    public CollectionId Collection { get; private set; }

    /// <summary>Formatted button label so UI Automation announces the full source link text.</summary>
    public string SourceCollectionButtonLabel => $"From {SourceCollectionName}";

    public bool HasSelectedEntry => SelectedEntry is not null;

    /// <summary>True while the background check of pinned cover files runs. Tests wait for it before changing cover files.</summary>
    internal bool IsCheckingCovers => _checking;

    public bool HasBoardSearch => !string.IsNullOrWhiteSpace(BoardSearchText);

    public bool HasBoardSearchMatches => BoardSearchMatchCount > 0;

    public bool IsCompactViewport => CompactViewportLevel > 0;

    public bool IsNotCompactViewport => CompactViewportLevel == 0;

    /// <summary>Effective card density (caps to Compact on short viewports without overwriting saved Density).</summary>
    public CardDensity EffectiveDensity => IsCompactViewport ? CardDensity.Compact : Density;

    /// <summary>Maintains true 2:3 (0.70) poster aspect ratio across all desktop densities and compact viewport levels.</summary>
    public double CardCoverWidth => Math.Round(CardCoverHeight * 0.70);

    /// <summary>
    /// Uses one unified compact board geometry across Compact Levels 1 and 2 so covers stay recognizable
    /// and two full tiers plus the unranked tray fit without reflow or clipping when Save-Failed appears at 960x540 DIP.
    /// </summary>
    public double CardCoverHeight => CompactViewportLevel >= 1
        ? 38
        : EffectiveDensity switch
        {
            CardDensity.Compact => 82,
            CardDensity.Large => 150,
            _ => 120,
        };

    /// <summary>Uses two lines on desktop boards and one wider line on compact boards so two full tiers and the tray stay visible without reflow.</summary>
    public double CardTitleMaxHeight => CompactViewportLevel >= 1
        ? 17
        : EffectiveDensity == CardDensity.Large ? 34 : 32;

    public double RowMinHeight => CompactViewportLevel >= 1 ? 72 : 122;

    public double CardWidth => CompactViewportLevel >= 1
        ? 104
        : Math.Max(92, CardCoverWidth + 16);

    public double TileWidth => CardWidth + 8;

    public double TileHeight => CompactViewportLevel >= 1 ? CardCoverHeight + 34 : CardCoverHeight + 50;

    public int DesiredThumbnailHeight => EffectiveDensity switch
    {
        CardDensity.Compact => ThumbnailLoader.PixelHeight,
        CardDensity.Standard => ThumbnailLoader.MediumPixelHeight,
        _ => ThumbnailLoader.LargePixelHeight,
    };

    public ObservableCollection<TierViewModel> Tiers { get; } = [];

    public TierViewModel Unranked { get; }

    /// <summary>
    /// Preset tier swatches: all 11 classic pastel colors (preserved by exact name) followed by 6 deeper
    /// dark-friendly swatches whose label text automatically flips to white via <see cref="TierColor.PreferredInk"/>.
    /// </summary>
    public IReadOnlyList<TierColorOption> ColorOptions { get; } =
    [
        new("Red", new TierColor(0xFF, 0x7F, 0x7F)),
        new("Orange", new TierColor(0xFF, 0xBF, 0x7F)),
        new("Amber", new TierColor(0xFF, 0xDF, 0x7F)),
        new("Yellow", new TierColor(0xFF, 0xFF, 0x7F)),
        new("Lime", new TierColor(0xBF, 0xFF, 0x7F)),
        new("Green", new TierColor(0x7F, 0xFF, 0x7F)),
        new("Cyan", new TierColor(0x7F, 0xFF, 0xFF)),
        new("Blue", new TierColor(0x7F, 0xBF, 0xFF)),
        new("Purple", new TierColor(0xBF, 0x7F, 0xFF)),
        new("Pink", new TierColor(0xFF, 0x7F, 0xDF)),
        new("Gray", new TierColor(0xC8, 0xC8, 0xC8)),
        new("Crimson", new TierColor(0xDC, 0x26, 0x26)),
        new("Deep Amber", new TierColor(0xD9, 0x77, 0x06)),
        new("Emerald", new TierColor(0x05, 0x96, 0x69)),
        new("Cobalt", new TierColor(0x25, 0x63, 0xEB)),
        new("Violet", new TierColor(0x7C, 0x3A, 0xED)),
        new("Slate", new TierColor(0x47, 0x55, 0x69)),
    ];

    internal ProjectServices Services => _project.Services;

    public ProjectViewModel Project => _project;

    /// <summary>Applies the current project revision, keeping tier and entry view models (and thumbnails) stable.</summary>
    public void Refresh(ProjectState state)
    {
        if (!state.Boards.TryGetValue(Id, out var board))
        {
            return;
        }

        Collection = board.Collection;
        Name = board.Name;
        if (board.Name != _loadedName)
        {
            // Only a saved change (including undo) replaces the rename box, so unrelated refreshes keep drafts.
            _loadedName = board.Name;
            RenameText = board.Name;
        }

        var collectionName = state.Collections.TryGetValue(board.Collection, out var collection) ? collection.Name : "deleted collection";
        SourceCollectionName = collectionName;
        var ranked = board.Placements.Values.Sum(p => p.Length);
        TotalCount = board.Entries.Length;
        RankedCount = ranked;
        UnrankedCount = board.Entries.Length - ranked;
        RankedPercent = TotalCount == 0 ? 0 : (100.0 * RankedCount / TotalCount);
        Summary = $"From '{collectionName}' · {board.Entries.Length} shows, {ranked} ranked, {board.Entries.Length - ranked} unranked";

        var tierViews = new List<TierViewModel>();
        foreach (var tier in board.Tiers)
        {
            if (!_tiers.TryGetValue(tier.Id, out var view))
            {
                view = new TierViewModel(this, tier.Id, tier.Name);
                _tiers[tier.Id] = view;
            }

            tierViews.Add(view);
        }

        foreach (var removed in _tiers.Keys.Where(k => !board.Placements.ContainsKey(k)).ToList())
        {
            _tiers.Remove(removed);
        }

        Sync(Tiers, tierViews);

        var byKey = board.Entries.ToDictionary(e => e.Key);
        foreach (var gone in _entries.Keys.Where(k => !byKey.ContainsKey(k)).ToList())
        {
            _entries.Remove(gone);
        }

        BoardEntryViewModel View(BoardEntryKey key) =>
            _entries.TryGetValue(key, out var existing) ? existing : _entries[key] = new BoardEntryViewModel(this, key);

        void Fill(TierViewModel target, IReadOnlyList<BoardEntryKey> keys, string label)
        {
            var views = keys.Select(View).ToList();
            Sync(target.Entries, views);
            for (var i = 0; i < keys.Count; i++)
            {
                var entry = byKey[keys[i]];
                var asset = entry.PinnedCover is { } hash && state.Assets.TryGetValue(hash, out var record) ? record : null;
                views[i].Update(entry, asset, asset is not null && _broken.Contains(asset.Hash), $"{label}, {i + 1} of {keys.Count}");
            }
        }

        for (var i = 0; i < board.Tiers.Length; i++)
        {
            var tier = board.Tiers[i];
            var view = _tiers[tier.Id];
            Fill(view, board.Placements[tier.Id], $"tier {tier.Name}");
            view.Update(tier.Name, tier.Color, i + 1);
        }

        Fill(Unranked, board.Unranked, "unranked");
        Unranked.Update("Unranked", null);

        if (SelectedEntry is { } selected && !byKey.ContainsKey(selected.Key))
        {
            SelectedEntry = null;
        }

        if (SelectedTier is { Id: { } selectedTier } && !board.Placements.ContainsKey(selectedTier))
        {
            SelectedTier = null;
        }
        else if (SelectedTier is { Id: { } shownTier } && board.Tiers.First(t => t.Id == shownTier) is var saved && _loadedTier != (saved.Id, saved.Name, saved.Color))
        {
            LoadTierEditor(SelectedTier);
        }

        if (SelectedTier is null && Tiers.Count > 0)
        {
            SelectedTier = Tiers[0];
        }

        UpdateSelectedTierFlags();
        ApplyBoardSearchHighlight();
        NotifyMutationCommands();

        var toCheck = board.Entries.Where(e => e.PinnedCover is { } h && !_checked.Contains(h) && state.Assets.ContainsKey(h)).Select(e => state.Assets[e.PinnedCover!.Value]).ToList();
        if (toCheck.Count > 0 && !_checking)
        {
            _ = CheckPinnedCoversAsync(toCheck);
        }
    }

    internal void OnEntrySelected(TierViewModel tier, BoardEntryViewModel entry)
    {
        foreach (var other in Tiers.Append(Unranked))
        {
            if (other != tier && other.SelectedEntry is not null)
            {
                other.SelectedEntry = null;
            }
        }

        SelectedEntry = entry;
    }

    /// <summary>Drag-and-drop target: places the show copy in a tier at an index, or returns it to the pool.</summary>
    public void Drop(BoardEntryViewModel entry, TierViewModel target, int index)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(target);
        if (target.IsPool)
        {
            Move(entry, "Return to unranked", b => BoardOperations.ReturnToUnranked(b, entry.Key));
            return;
        }

        var tier = target.Id!.Value;
        Move(entry, $"Move to tier {target.Name}", b =>
        {
            var current = b.Placements[tier];
            var from = current.IndexOf(entry.Key);
            var destination = from >= 0 && from < index ? index - 1 : index;
            return BoardOperations.MoveToTier(b, entry.Key, tier, Math.Clamp(destination, 0, from >= 0 ? current.Length - 1 : current.Length));
        });
    }

    /// <summary>Opens the LiveChart anime details and duplication dialog for <paramref name="entry"/>.</summary>
    [RelayCommand]
    private void OpenEntryDetails(BoardEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        var details = new BoardEntryDetailsViewModel(this, entry, _project.Session.Current);
        _project.App.Dialogs?.ShowBoardEntryDetails(details);
    }

    /// <summary>Keyboard equivalent (Enter) to open the details and duplication dialog for the selected board card.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedEntry))]
    private void OpenSelectedEntryDetails() => OpenEntryDetails(SelectedEntry);

    /// <summary>Creates a numbered duplicate copy of <paramref name="item"/> in the unranked pool right next to the original.</summary>
    public BoardEntry? DuplicateEntry(LiveChartId item)
    {
        BoardEntry? created = null;
        if (Edit("Duplicate entry", b => BoardOperations.DuplicateEntry(b, item, out created)) && created is not null)
        {
            _project.Status = $"Added copy #{created.CopyNumber} of '{created.PinnedTitle}' in Unranked.";
            return created;
        }

        return null;
    }

    /// <summary>Removes a duplicated copy (<see cref="BoardEntryKey.CopyNumber"/> &gt; 1) as one undoable command.</summary>
    public bool RemoveDuplicateEntry(BoardEntryKey key)
    {
        if (!key.IsDuplicate)
        {
            return false;
        }

        if (Edit($"Remove copy #{key.CopyNumber}", b => BoardOperations.RemoveDuplicate(b, key)))
        {
            _project.Status = $"Removed copy #{key.CopyNumber}. Use Undo to restore it.";
            return true;
        }

        return false;
    }

    [RelayCommand]
    private void OpenSourceCollection() => _project.ShowCollection(Collection);

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
        foreach (var entry in _entries.Values)
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

        _project.App.Preferences.Update(s => s with { BoardDensity = value });
        foreach (var entry in _entries.Values)
        {
            entry.RefreshThumbnailResolution();
        }
    }

    [RelayCommand]
    private void ToggleTray()
    {
        TrayCollapsed = !TrayCollapsed;
        _project.App.Preferences.Update(s => s with { BoardTrayCollapsed = TrayCollapsed });
    }

    [RelayCommand]
    private void ToggleInspector()
    {
        InspectorOpen = !InspectorOpen;
        _project.App.Preferences.Update(s => s with { BoardInspectorOpen = InspectorOpen });
    }

    [RelayCommand]
    private void CloseInspector()
    {
        if (InspectorOpen)
        {
            InspectorOpen = false;
            _project.App.Preferences.Update(s => s with { BoardInspectorOpen = false });
        }
    }

    [RelayCommand]
    private void SelectTierAndInspect(TierViewModel? tier)
    {
        if (tier is null || tier.IsPool)
        {
            return;
        }

        SelectedTier = tier;
        InspectorTabIndex = 1;
        InspectorOpen = true;
        _project.App.Preferences.Update(s => s with { BoardInspectorOpen = true });
    }

    [RelayCommand]
    private void OpenTierEditor()
    {
        if (SelectedTier is null && Tiers.Count > 0)
        {
            SelectedTier = Tiers[0];
        }

        InspectorTabIndex = 1;
        InspectorOpen = true;
        _project.App.Preferences.Update(s => s with { BoardInspectorOpen = true });
    }

    [RelayCommand(CanExecute = nameof(HasTier))]
    private void SelectTierColor(TierColorOption? color)
    {
        if (color is null || SelectedTier is not { Id: { } id } tier || _project.EditsSuspended)
        {
            return;
        }

        // Preserve the tier's current saved name so clicking a swatch never commits half-typed draft text.
        if (Edit("Change tier color", b => BoardOperations.EditTier(b, id, tier.Name, color.Value)))
        {
            TierColor = color;
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

    [RelayCommand]
    private void ClearBoardSearch() => BoardSearchText = string.Empty;

    [RelayCommand(CanExecute = nameof(HasBoardSearchMatches))]
    private void NextSearchMatch() => FocusSearchMatch(+1);

    [RelayCommand(CanExecute = nameof(HasBoardSearchMatches))]
    private void PreviousSearchMatch() => FocusSearchMatch(-1);

    /// <summary>Moves the selected show to an explicit tier target (supports boards with more than 9 tiers).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveSelectedToTier(TierViewModel? target)
    {
        if (SelectedEntry is { } entry && target is not null)
        {
            Drop(entry, target, target.Entries.Count);
        }
    }

    /// <summary>Keys 1–9: move the selected show to the end of that tier.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveToTier(string? number)
    {
        if (SelectedEntry is not { } entry || !int.TryParse(number, out var n) || n < 1 || n > Tiers.Count)
        {
            return;
        }

        var target = Tiers[n - 1];
        Drop(entry, target, target.Entries.Count);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveToUnranked()
    {
        if (SelectedEntry is { } entry)
        {
            Drop(entry, Unranked, 0);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveLeft() => Shift(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveRight() => Shift(+1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveToTierAbove() => Step(-1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void MoveToTierBelow() => Step(+1);

    private bool HasSelection() => SelectedEntry is not null && !_project.EditsSuspended;

    private bool CanEdit() => !_project.EditsSuspended;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Rename()
    {
        if (!string.IsNullOrWhiteSpace(RenameText) && RenameText.Trim() != Name)
        {
            Edit("Rename board", b => BoardOperations.Rename(b, RenameText));
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Delete()
    {
        if (_project.App.Dialogs?.Confirm("Delete board", $"Delete the board '{Name}'? Its collection and covers are not affected. You can undo this until the project is closed.") != true)
        {
            return;
        }

        if (_project.Execute($"Delete board '{Name}'", s => s.WithoutBoard(Id)) is SessionResult.Accepted)
        {
            _project.Status = $"Deleted board '{Name}'. Use Undo to restore it.";
        }
    }

    /// <summary>Exports the saved board revision to PNG; verifies intact covers on disk before showing the scope preview.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task ExportPngAsync()
    {
        ProjectState preflightSaved;
        BoardState preflightBoard;
        IReadOnlySet<AssetHash> intact;
        while (true)
        {
            if (await _project.SavedRevisionAsync() is not { } candidateSaved || !candidateSaved.Boards.TryGetValue(Id, out var candidateBoard))
            {
                return;
            }

            var pinnedAssets = candidateBoard.Entries
                .Where(e => e.PinnedCover is { } h && candidateSaved.Assets.ContainsKey(h))
                .Select(e => candidateSaved.Assets[e.PinnedCover!.Value])
                .Distinct()
                .ToList();
            try
            {
                intact = await AssetChecks.IntactAsync(Services.Assets, pinnedAssets, _project.Lifetime);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_project.Lifetime.IsCancellationRequested)
            {
                return;
            }

            // Re-check after async disk hashing so the preview always reflects the latest saved board state.
            if (await _project.SavedRevisionAsync() is not { } latestSaved || !latestSaved.Boards.TryGetValue(Id, out var latestBoard))
            {
                return;
            }

            if (ReferenceEquals(latestBoard, candidateBoard))
            {
                preflightSaved = candidateSaved;
                preflightBoard = candidateBoard;
                break;
            }
        }

        var preview = new PngExportPreviewViewModel(preflightBoard, preflightSaved.Assets, intact, IncludeUnrankedInPng);
        if (_project.App.Dialogs?.ShowPngExportOptions(preview) == false)
        {
            if (preview.OpenUpdateBoardRequested)
            {
                await UpdateFromCollectionCoreAsync(offerCollectionFallback: true);
            }

            return;
        }

        IncludeUnrankedInPng = preview.IncludeUnranked;

        var scope = IncludeUnrankedInPng ? "tiers and unranked shows" : "tiers only, unranked shows omitted";
        var suggested = (ProjectFolderName.From(preflightBoard.Name) ?? "board") + ".png";
        if (_project.App.Dialogs?.ChooseSaveFile($"Export '{preflightBoard.Name}' as PNG ({scope})", "PNG image (*.png)|*.png", suggested) is not { } choice)
        {
            return;
        }

        // Confirm no save failure occurred meanwhile, then export the exact saved revision approved in the preview.
        if (await _project.SavedRevisionAsync() is not { } postDialogSaved || !postDialogSaved.Boards.ContainsKey(Id))
        {
            return;
        }

        try
        {
            await BoardPngExporter.ExportAsync(preflightBoard, preflightSaved.Assets, Services.Assets, choice.Path, IncludeUnrankedInPng, choice.ReplaceExisting, _project.Lifetime);
            _project.Status = $"Exported '{preflightBoard.Name}' ({scope}) to {choice.Path}.";
        }
        catch (ExportException ex)
        {
            _project.App.Dialogs?.Inform("PNG not exported", ex.Message, ex.Affected.IsEmpty ? null : ex.Affected);
            _project.Status = "PNG not exported: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddTier()
    {
        var name = string.IsNullOrWhiteSpace(TierNameText) ? NextDefaultTierName() : TierNameText.Trim();
        var index = SelectedTier is { IsPool: false } selected ? Tiers.IndexOf(selected) + 1 : Tiers.Count;
        var tier = new Tier(TierId.New(), name, TierColor.Value);
        if (Edit($"Add tier {name}", b => BoardOperations.AddTier(b, tier, index)))
        {
            SelectedTier = _tiers.GetValueOrDefault(tier.Id);
        }
    }

    /// <summary>Appends a fresh uniquely named tier with an unused color at the bottom and opens the tier editor on it.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AppendNewTier()
    {
        var name = NextDefaultTierName();
        var color = NextUnusedTierColor();
        var tier = new Tier(TierId.New(), name, color);
        if (Edit($"Add tier {name}", b => BoardOperations.AddTier(b, tier, Tiers.Count)))
        {
            SelectedTier = _tiers.GetValueOrDefault(tier.Id);
            InspectorTabIndex = 1;
            InspectorOpen = true;
            _project.App.Preferences.Update(s => s with { BoardInspectorOpen = true });
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddTierBelow(TierViewModel? anchor)
    {
        var index = anchor is { IsPool: false } ? Tiers.IndexOf(anchor) + 1 : Tiers.Count;
        var name = NextDefaultTierName();
        var color = NextUnusedTierColor();
        var tier = new Tier(TierId.New(), name, color);
        if (Edit($"Add tier {name}", b => BoardOperations.AddTier(b, tier, index)))
        {
            SelectedTier = _tiers.GetValueOrDefault(tier.Id);
            InspectorTabIndex = 1;
            InspectorOpen = true;
            _project.App.Preferences.Update(s => s with { BoardInspectorOpen = true });
        }
    }

    [RelayCommand(CanExecute = nameof(HasTier))]
    private void EditTier()
    {
        if (SelectedTier is { Id: { } id } && !string.IsNullOrWhiteSpace(TierNameText))
        {
            Edit("Edit tier", b => BoardOperations.EditTier(b, id, TierNameText, TierColor.Value));
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveTierUp))]
    private void MoveTierUp() => MoveTier(-1);

    [RelayCommand(CanExecute = nameof(CanMoveTierDown))]
    private void MoveTierDown() => MoveTier(+1);

    private bool CanMoveTierUp() => HasTier() && SelectedTier is not null && Tiers.IndexOf(SelectedTier) > 0;

    private bool CanMoveTierDown() => HasTier() && SelectedTier is not null && Tiers.IndexOf(SelectedTier) is >= 0 and var i && i < Tiers.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveTierItemUp))]
    private void MoveTierItemUp(TierViewModel? tier)
    {
        if (tier is { IsPool: false })
        {
            SelectedTier = tier;
            MoveTier(-1);
        }
    }

    private bool CanMoveTierItemUp(TierViewModel? tier) => CanEdit() && tier is { IsPool: false } && Tiers.IndexOf(tier) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveTierItemDown))]
    private void MoveTierItemDown(TierViewModel? tier)
    {
        if (tier is { IsPool: false })
        {
            SelectedTier = tier;
            MoveTier(+1);
        }
    }

    private bool CanMoveTierItemDown(TierViewModel? tier) => CanEdit() && tier is { IsPool: false } && Tiers.IndexOf(tier) is >= 0 and var i && i < Tiers.Count - 1;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteTierItem(TierViewModel? tier)
    {
        if (tier is { IsPool: false })
        {
            SelectedTier = tier;
            DeleteTier();
        }
    }

    [RelayCommand(CanExecute = nameof(HasTier))]
    private void DeleteTier()
    {
        if (SelectedTier is not { Id: { } id } tier)
        {
            return;
        }

        if (tier.Entries.Count > 0 && _project.App.Dialogs?.Confirm("Delete tier",
                $"Delete tier '{tier.Name}'? Its {(tier.Entries.Count == 1 ? "1 show returns" : $"{tier.Entries.Count} shows return")} to Unranked in original collection order. You can undo this.") != true)
        {
            return;
        }

        Edit($"Delete tier {tier.Name}", b => BoardOperations.DeleteTier(b, id));
    }

    private bool HasTier() => SelectedTier is { IsPool: false } && !_project.EditsSuspended;

    /// <summary>Reviewed update from the source collection: additions, removals, and presentation, as one command.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task UpdateFromCollectionAsync() => UpdateFromCollectionCoreAsync(offerCollectionFallback: false);

    private async Task UpdateFromCollectionCoreAsync(bool offerCollectionFallback)
    {
        var state = _project.Session.Current;
        if (!state.Boards.TryGetValue(Id, out var board) || !state.Collections.TryGetValue(board.Collection, out var collection))
        {
            _project.Status = "The board's collection no longer exists.";
            if (offerCollectionFallback)
            {
                _project.App.Dialogs?.Inform("Source collection unavailable",
                    "This board's source collection no longer exists, so covers cannot be updated from it. Move shows without covers to Unranked and export Tiers only.");
            }

            return;
        }

        var assets = collection.Entries.Where(e => e.Cover is not null).Select(e => state.Assets[e.Cover!.Asset])
            .Concat(board.Entries.Where(e => e.PinnedCover is { } h && state.Assets.ContainsKey(h)).Select(e => state.Assets[e.PinnedCover!.Value])).ToList();
        IReadOnlySet<AssetHash> intact;
        try
        {
            intact = await AssetChecks.IntactAsync(Services.Assets, assets, _project.Lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_project.Lifetime.IsCancellationRequested)
        {
            return;
        }

        var review = new BoardUpdateViewModel(board, collection, state, intact.Contains);
        if (!review.HasChanges)
        {
            _project.Status = $"'{board.Name}' already matches its collection.";
            if (offerCollectionFallback && _project.App.Dialogs?.Confirm("Repair covers in source collection?",
                    $"'{board.Name}' already matches '{collection.Name}', so the missing or damaged covers must be fetched or repaired in the source collection first. Open '{collection.Name}' now?") == true)
            {
                _project.ShowCollection(Collection);
            }

            return;
        }

        if (_project.App.Dialogs?.ShowBoardUpdate(review) != true)
        {
            _project.Status = "Board update cancelled. Nothing changed.";
            return;
        }

        var removals = review.SelectedRemovals;
        if (removals.Count > 0 && _project.App.Dialogs?.Confirm("Remove shows from board",
                $"Remove {(removals.Count == 1 ? "1 show" : $"{removals.Count} shows")} from '{board.Name}', including tier placements? Declining cancels the whole update.") != true)
        {
            _project.Status = "Board update cancelled. Nothing changed.";
            return;
        }

        // Covers can be repaired or damaged in place during the review without changing the board or collection.
        IReadOnlySet<AssetHash> recheck;
        try
        {
            recheck = await AssetChecks.IntactAsync(Services.Assets, assets, _project.Lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_project.Lifetime.IsCancellationRequested)
        {
            return;
        }

        if (!recheck.SetEquals(intact))
        {
            _project.Status = "Cover files changed during the review, so nothing was applied. Open the update again to review the current state.";
            return;
        }

        var additions = review.SelectedAdditions;
        var result = _project.Execute("Update board", s =>
        {
            // The preview and cover checks describe the reviewed revisions; anything newer needs a fresh review.
            if (!s.Boards.TryGetValue(Id, out var currentBoard) || !s.Collections.TryGetValue(board.Collection, out var currentCollection)
                || !ReferenceEquals(currentBoard, review.Board) || !ReferenceEquals(currentCollection, review.Collection))
            {
                throw new InvalidOperationException("The board or its collection changed during the review, so nothing was applied. Open the update again to review the current state.");
            }

            return s.WithBoard(BoardOperations.ApplyUpdate(currentBoard, currentCollection, additions, removals, review.Presentation.Value, e => s.ResolveTitle(currentCollection, e), intact.Contains));
        });
        if (Shown(result))
        {
            _project.Status = $"Board updated: {additions.Count} added, {removals.Count} removed.";
        }
    }

    partial void OnBoardSearchTextChanged(string value)
    {
        _searchMatchCursor = -1;
        ApplyBoardSearchHighlight();
    }

    partial void OnTrayHeightChanged(double value)
    {
        if (_initialized && double.IsFinite(value) && value >= 74)
        {
            _project.App.Preferences.Update(s => s with { BoardTrayHeight = value });
        }
    }

    partial void OnSelectedTierChanged(TierViewModel? value)
    {
        LoadTierEditor(value);
        UpdateSelectedTierFlags();
        SelectTierColorCommand.NotifyCanExecuteChanged();
        MoveTierUpCommand.NotifyCanExecuteChanged();
        MoveTierDownCommand.NotifyCanExecuteChanged();
    }

    private void NotifyMutationCommands()
    {
        BeginInlineRenameCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ExportPngCommand.NotifyCanExecuteChanged();
        AddTierCommand.NotifyCanExecuteChanged();
        AppendNewTierCommand.NotifyCanExecuteChanged();
        AddTierBelowCommand.NotifyCanExecuteChanged();
        EditTierCommand.NotifyCanExecuteChanged();
        SelectTierColorCommand.NotifyCanExecuteChanged();
        MoveTierUpCommand.NotifyCanExecuteChanged();
        MoveTierDownCommand.NotifyCanExecuteChanged();
        MoveTierItemUpCommand.NotifyCanExecuteChanged();
        MoveTierItemDownCommand.NotifyCanExecuteChanged();
        DeleteTierCommand.NotifyCanExecuteChanged();
        DeleteTierItemCommand.NotifyCanExecuteChanged();
        UpdateFromCollectionCommand.NotifyCanExecuteChanged();
        MoveToTierCommand.NotifyCanExecuteChanged();
        MoveToUnrankedCommand.NotifyCanExecuteChanged();
        MoveLeftCommand.NotifyCanExecuteChanged();
        MoveRightCommand.NotifyCanExecuteChanged();
        MoveToTierAboveCommand.NotifyCanExecuteChanged();
        MoveToTierBelowCommand.NotifyCanExecuteChanged();
        MoveSelectedToTierCommand.NotifyCanExecuteChanged();
    }

    private string NextDefaultTierName()
    {
        var existing = Tiers.Select(t => t.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!existing.Contains("New tier"))
        {
            return "New tier";
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"New tier {n}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private TierColor NextUnusedTierColor()
    {
        var used = Tiers.Select(t => t.Color).ToHashSet();
        foreach (var option in ColorOptions)
        {
            if (!used.Contains(option.Value))
            {
                return option.Value;
            }
        }

        return ColorOptions[Tiers.Count % ColorOptions.Count].Value;
    }

    private void UpdateSelectedTierFlags()
    {
        foreach (var tier in Tiers)
        {
            tier.IsSelectedTier = ReferenceEquals(tier, SelectedTier);
        }
    }

    private void ApplyBoardSearchHighlight()
    {
        var query = BoardSearchText.Trim();
        var matches = 0;
        var rowsWithMatches = 0;
        foreach (var row in Tiers.Append(Unranked))
        {
            var rowMatches = 0;
            foreach (var entry in row.Entries)
            {
                entry.UpdateSearchHighlight(query);
                if (entry.IsSearchMatch)
                {
                    matches++;
                    rowMatches++;
                }
            }

            if (rowMatches > 0)
            {
                rowsWithMatches++;
            }
        }

        BoardSearchMatchCount = string.IsNullOrWhiteSpace(query) ? 0 : matches;
        BoardSearchMatchSummary = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : matches == 0
                ? "No matches"
                : $"{matches} match{(matches == 1 ? string.Empty : "es")} in {rowsWithMatches} row{(rowsWithMatches == 1 ? string.Empty : "s")}";
    }

    private void FocusSearchMatch(int delta)
    {
        var allMatches = Tiers.Append(Unranked).SelectMany(r => r.Entries).Where(e => e.IsSearchMatch).ToList();
        if (allMatches.Count == 0)
        {
            return;
        }

        _searchMatchCursor = _searchMatchCursor < 0
            ? (delta >= 0 ? 0 : allMatches.Count - 1)
            : (_searchMatchCursor + delta + allMatches.Count) % allMatches.Count;
        var target = allMatches[_searchMatchCursor];
        if (TierOf(target) is { } row)
        {
            if (row.IsPool && TrayCollapsed)
            {
                TrayCollapsed = false;
            }

            row.SelectedEntry = target;
            SelectionMoved?.Invoke(this, target);
        }
    }

    private void LoadTierEditor(TierViewModel? tier)
    {
        if (tier is { Id: { } id })
        {
            _loadedTier = (id, tier.Name, tier.Color);
            TierNameText = tier.Name;
            TierColor = ColorOptions.FirstOrDefault(o => o.Value == tier.Color) ?? TierColor;
        }
        else
        {
            _loadedTier = null;
        }
    }

    /// <summary>Forgets earlier cover-file verdicts so repaired or newly damaged pins are shown as they are now.</summary>
    internal void RecheckCovers()
    {
        // A check still running belongs to an older generation; its results are discarded when it finishes.
        // Keep _broken intact until CheckPinnedCoversAsync confirms repairs so damaged cards never flash blank mats.
        _checkGeneration++;
        _checking = false;
        _checked.Clear();
        foreach (var entry in _entries.Values)
        {
            entry.ResetThumbnailFailure();
        }

        Refresh(_project.Session.Current);
    }

    private void Shift(int delta)
    {
        if (SelectedEntry is not { } entry || TierOf(entry) is not { IsPool: false, Id: { } tier } row)
        {
            return;
        }

        var index = row.Entries.IndexOf(entry) + delta;
        if (index >= 0 && index < row.Entries.Count)
        {
            Move(entry, "Reorder within tier", b => BoardOperations.MoveToTier(b, entry.Key, tier, index));
        }
    }

    private void Step(int delta)
    {
        if (SelectedEntry is not { } entry || TierOf(entry) is not { } row)
        {
            return;
        }

        // Rows from top to bottom are the tiers followed by the pool.
        var rows = Tiers.Append(Unranked).ToList();
        var target = rows.IndexOf(row) + delta;
        if (target >= 0 && target < rows.Count)
        {
            Drop(entry, rows[target], rows[target].Entries.Count);
        }
    }

    private void MoveTier(int delta)
    {
        if (SelectedTier is not { Id: { } id } tier)
        {
            return;
        }

        var index = Tiers.IndexOf(tier) + delta;
        if (index >= 0 && index < Tiers.Count)
        {
            Edit("Move tier", b => BoardOperations.MoveTier(b, id, index));
        }
    }

    private TierViewModel? TierOf(BoardEntryViewModel entry) => Tiers.Append(Unranked).FirstOrDefault(t => t.Entries.Contains(entry));

    private void Move(BoardEntryViewModel entry, string label, Func<BoardState, BoardState> edit)
    {
        if (Edit(label, edit))
        {
            if (TierOf(entry) is { } row)
            {
                row.SelectedEntry = entry;
            }

            SelectionMoved?.Invoke(this, entry);
        }
    }

    /// <summary>Runs one board command and shows its result at once, without waiting for the next refresh pass.</summary>
    private bool Edit(string label, Func<BoardState, BoardState> edit) => Shown(_project.Execute(label, s => s.WithBoard(edit(s.Board(Id)))));

    private bool Shown(SessionResult result)
    {
        if (result is not SessionResult.Accepted)
        {
            return false;
        }

        Refresh(_project.Session.Current);
        return true;
    }

    /// <summary>Checks pinned cover files in the background so damaged ones show as such. Handles its own failures.</summary>
    private async Task CheckPinnedCoversAsync(List<AssetRecord> assets)
    {
        var generation = _checkGeneration;
        _checking = true;
        IReadOnlySet<AssetHash> intact;
        try
        {
            intact = await AssetChecks.IntactAsync(Services.Assets, assets, _project.Lifetime);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (generation == _checkGeneration)
            {
                _checking = false;
            }
        }

        if (generation != _checkGeneration || _project.Lifetime.IsCancellationRequested)
        {
            return;
        }

        foreach (var asset in assets)
        {
            _checked.Add(asset.Hash);
            if (intact.Contains(asset.Hash))
            {
                _broken.Remove(asset.Hash);
            }
            else
            {
                _broken.Add(asset.Hash);
            }
        }

        Refresh(_project.Session.Current);
    }

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired)
        where T : class
    {
        for (var i = 0; i < desired.Count; i++)
        {
            var from = target.IndexOf(desired[i]);
            if (from == i)
            {
                continue;
            }

            if (from >= 0)
            {
                target.Move(from, i);
            }
            else
            {
                target.Insert(i, desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
