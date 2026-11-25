using System.Collections.ObjectModel;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeTierlist.Desktop.ViewModels;

public sealed partial class SelectableShow(LiveChartId item, string title) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = true;

    public LiveChartId Item { get; } = item;

    public string Title { get; } = title;

    public override string ToString() => Title;
}

public sealed record PresentationOption(string Label, PresentationUpdate Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The reviewed board update: addition and removal candidates chosen separately, plus an explicit presentation
/// choice whose effect is previewed. Applying it is one undoable command; removals still need confirmation.
/// </summary>
public sealed partial class BoardUpdateViewModel : ObservableObject
{
    private readonly BoardState _board;
    private readonly CollectionState _collection;
    private readonly ProjectState _state;
    private readonly Func<AssetHash, bool> _isAssetValid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsKeepPinned), nameof(IsFillMissing), nameof(IsFullRefresh), nameof(ApplyButtonLabel), nameof(HasSelectedChanges))]
    private PresentationOption _presentation;

    [ObservableProperty]
    private string _presentationPreview = string.Empty;

    /// <summary>Creates a reviewable update proposal comparing a board with its source collection.</summary>
    public BoardUpdateViewModel(BoardState board, CollectionState collection, ProjectState state, Func<AssetHash, bool> isAssetValid)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(state);
        _board = board;
        _collection = collection;
        _state = state;
        _isAssetValid = isAssetValid;
        var proposal = BoardOperations.ProposeUpdate(board, collection);
        Additions = proposal.AdditionCandidates.Select(i => new SelectableShow(i, state.ResolveTitle(collection, collection.Entry(i)).Text)).ToList();
        Removals = proposal.RemovalCandidates.Select(i =>
        {
            var copies = board.CopiesOf(i);
            var title = copies[0].PinnedTitle;
            return new SelectableShow(i, copies.Length > 1 ? $"{title} ({copies.Length} copies)" : title);
        }).ToList();
        _presentation = PresentationOptions[0];
        foreach (var addition in Additions)
        {
            addition.PropertyChanged += (_, _) => NotifySelectionSummary();
        }

        foreach (var removal in Removals)
        {
            removal.PropertyChanged += (_, _) =>
            {
                Recompute();
                NotifySelectionSummary();
            };
        }

        Recompute();
        HasChanges = Additions.Count > 0 || Removals.Count > 0 || PresentationOptions.Skip(1).Any(o => Changes(o.Value) > 0);
    }

    /// <summary>The board and collection revisions this review was built from; applying requires them unchanged.</summary>
    public BoardState Board => _board;

    public CollectionState Collection => _collection;

    /// <summary>Each retained show whose pinned title or cover the chosen presentation update changes, with before and after.</summary>
    public ObservableCollection<string> PresentationChanges { get; } = [];

    public string Heading => $"Update board '{_board.Name}' from '{_collection.Name}'";

    /// <summary>Included collection shows not on the board, in collection order. They are added unranked.</summary>
    public IReadOnlyList<SelectableShow> Additions { get; }

    /// <summary>Board shows whose collection entry is excluded. Removing them also removes their placements.</summary>
    public IReadOnlyList<SelectableShow> Removals { get; }

    public IReadOnlyList<PresentationOption> PresentationOptions { get; } =
    [
        new("Keep pinned titles and covers", PresentationUpdate.None),
        new("Fill missing or damaged covers only", PresentationUpdate.FillMissingCovers),
        new("Refresh all titles and covers from the collection", PresentationUpdate.FullRefresh),
    ];

    public bool HasChanges { get; }

    /// <summary>True when at least one addition, removal, or presentation update is currently selected to apply.</summary>
    public bool HasSelectedChanges => SelectedAdditions.Count > 0 || SelectedRemovals.Count > 0 || PresentationChanges.Count > 0;

    public IReadOnlyCollection<LiveChartId> SelectedAdditions => Additions.Where(a => a.IsSelected).Select(a => a.Item).ToList();

    public IReadOnlyCollection<LiveChartId> SelectedRemovals => Removals.Where(r => r.IsSelected).Select(r => r.Item).ToList();

    public string AdditionsSummary => $"Add shows included in collection ({SelectedAdditions.Count} of {Additions.Count} selected; added unranked)";

    public string RemovalsSummary => $"Remove shows excluded from collection ({SelectedRemovals.Count} of {Removals.Count} selected; asks confirmation)";

    public bool IsKeepPinned
    {
        get => Presentation.Value == PresentationUpdate.None;
        set { if (value) { Presentation = PresentationOptions[0]; } }
    }

    public bool IsFillMissing
    {
        get => Presentation.Value == PresentationUpdate.FillMissingCovers;
        set { if (value) { Presentation = PresentationOptions[1]; } }
    }

    public bool IsFullRefresh
    {
        get => Presentation.Value == PresentationUpdate.FullRefresh;
        set { if (value) { Presentation = PresentationOptions[2]; } }
    }

    public string ApplyButtonLabel
    {
        get
        {
            var parts = new List<string>();
            if (SelectedAdditions.Count > 0)
            {
                parts.Add($"+{SelectedAdditions.Count}");
            }

            if (SelectedRemovals.Count > 0)
            {
                parts.Add($"-{SelectedRemovals.Count}");
            }

            if (PresentationChanges.Count > 0)
            {
                parts.Add($"{PresentationChanges.Count} updated");
            }

            return parts.Count == 0 ? "_Apply update" : $"_Apply update ({string.Join(", ", parts)})";
        }
    }

    [RelayCommand]
    private void SelectAllAdditions() => SetAll(Additions, true);

    [RelayCommand]
    private void SelectNoAdditions() => SetAll(Additions, false);

    [RelayCommand]
    private void SelectAllRemovals() => SetAll(Removals, true);

    [RelayCommand]
    private void SelectNoRemovals() => SetAll(Removals, false);

    partial void OnPresentationChanged(PresentationOption value) => Recompute();

    private static void SetAll(IReadOnlyList<SelectableShow> shows, bool selected)
    {
        foreach (var show in shows)
        {
            show.IsSelected = selected;
        }
    }

    private void NotifySelectionSummary()
    {
        OnPropertyChanged(nameof(AdditionsSummary));
        OnPropertyChanged(nameof(RemovalsSummary));
        OnPropertyChanged(nameof(ApplyButtonLabel));
        OnPropertyChanged(nameof(HasSelectedChanges));
    }

    private int Changes(PresentationUpdate presentation)
    {
        var updated = BoardOperations.UpdatePresentation(_board, _collection, presentation, e => _state.ResolveTitle(_collection, e), _isAssetValid);
        return updated.Entries.Zip(_board.Entries).Count(p => p.First != p.Second);
    }

    private void Recompute()
    {
        PresentationChanges.Clear();
        var presentation = Presentation.Value;
        if (presentation == PresentationUpdate.None)
        {
            PresentationPreview = "Pinned titles and covers stay as they are.";
            OnPropertyChanged(nameof(ApplyButtonLabel));
            OnPropertyChanged(nameof(HasSelectedChanges));
            return;
        }

        var removed = SelectedRemovals.ToHashSet();
        var updated = BoardOperations.UpdatePresentation(_board, _collection, presentation, e => _state.ResolveTitle(_collection, e), _isAssetValid);
        foreach (var (after, before) in updated.Entries.Zip(_board.Entries))
        {
            if (after == before || removed.Contains(before.Item))
            {
                continue;
            }

            var parts = new List<string>();
            if (after.PinnedTitle != before.PinnedTitle)
            {
                parts.Add($"title \u201C{before.PinnedTitle}\u201D becomes \u201C{after.PinnedTitle}\u201D");
            }

            if (after.PinnedCover != before.PinnedCover)
            {
                var wasValid = before.PinnedCover is { } old && _isAssetValid(old);
                parts.Add((before.PinnedCover, after.PinnedCover) switch
                {
                    (null, _) => "missing cover filled from the collection",
                    (_, null) when wasValid => "cover removed because the collection has no valid cover",
                    (_, null) => "damaged cover cleared to missing because the collection has no valid cover",
                    _ when wasValid => "cover replaced with the collection's current, different cover",
                    _ => "damaged cover replaced with the collection's current, different cover",
                });
            }

            var label = before.IsDuplicate ? $"{before.PinnedTitle} (copy #{before.CopyNumber})" : before.PinnedTitle;
            PresentationChanges.Add($"{label}: {string.Join("; ", parts)}");
        }

        PresentationPreview = PresentationChanges.Count == 0 ? "No pinned title or cover changes." : $"{PresentationChanges.Count} show(s) change:";
        OnPropertyChanged(nameof(ApplyButtonLabel));
        OnPropertyChanged(nameof(HasSelectedChanges));
    }
}
