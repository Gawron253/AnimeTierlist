using System.Collections.ObjectModel;
using System.Globalization;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Exports;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>
/// Pre-flight PNG export options and deterministic layout preview built from the board's current saved revision
/// before the native SaveFileDialog opens.
/// </summary>
public sealed partial class PngExportPreviewViewModel : ObservableObject
{
    private readonly BoardState _board;
    private readonly IReadOnlySet<AssetHash> _intactAssets;
    private readonly IReadOnlyDictionary<AssetHash, AssetRecord> _assets;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcludeUnranked), nameof(ScopeSummary), nameof(DimensionsText), nameof(HasBlockers), nameof(CanContinue), nameof(BlockerSummary))]
    private bool _includeUnranked;

    /// <summary>Creates a PNG export preview for <paramref name="board"/> with live dimension and cover checks.</summary>
    public PngExportPreviewViewModel(
        BoardState board,
        IReadOnlyDictionary<AssetHash, AssetRecord> assets,
        IReadOnlySet<AssetHash> intactAssets,
        bool includeUnranked)
    {
        _board = board;
        _assets = assets;
        _intactAssets = intactAssets;
        _includeUnranked = includeUnranked;
        Recompute();
    }

    public string BoardName => _board.Name;

    public int TierCount => _board.Tiers.Length;

    public int RankedCount => _board.Placements.Values.Sum(p => p.Length);

    public int UnrankedCount => _board.Unranked.Length;

    public bool ExcludeUnranked
    {
        get => !IncludeUnranked;
        set
        {
            if (value)
            {
                IncludeUnranked = false;
            }
        }
    }

    public string IncludeUnrankedOptionLabel => UnrankedCount == 1
        ? "All tiers and Unranked section (1 unranked)"
        : $"All tiers and Unranked section ({UnrankedCount} unranked)";

    public string ScopeSummary => IncludeUnranked
        ? $"{TierCount} {(TierCount == 1 ? "tier" : "tiers")} · {RankedCount} ranked + {UnrankedCount} unranked ({RankedCount + UnrankedCount} total shows)"
        : $"{TierCount} {(TierCount == 1 ? "tier" : "tiers")} · {RankedCount} ranked {(RankedCount == 1 ? "show" : "shows")} (unranked omitted)";

    [ObservableProperty]
    private string _dimensionsText = string.Empty;

    [ObservableProperty]
    private bool _exceedsPixelLimit;

    public ObservableCollection<string> MissingOrBrokenInScope { get; } = [];

    public bool HasBlockers => MissingOrBrokenInScope.Count > 0 || ExceedsPixelLimit;

    public bool CanOpenUpdateBoard => MissingOrBrokenInScope.Count > 0;

    /// <summary>Set when the user chooses the recovery shortcut to open the Board Update dialog from the preview.</summary>
    public bool OpenUpdateBoardRequested { get; private set; }

    public bool CanContinue => !HasBlockers;

    /// <summary>Marks that the user asked to close the PNG preview and open the reviewed Board Update dialog.</summary>
    public void RequestUpdateBoard() => OpenUpdateBoardRequested = true;

    public string BlockerSummary => ExceedsPixelLimit
        ? "This scope exceeds the 40,000,000-pixel PNG safety limit. Export tiers without unranked shows, or split the board."
        : MissingOrBrokenInScope.Count > 0
            ? $"{MissingOrBrokenInScope.Count} {(MissingOrBrokenInScope.Count == 1 ? "show in this scope has" : "shows in this scope have")} a missing or damaged cover. Repair covers, move affected shows to Unranked and choose Tiers only, or update the board before exporting."
            : "All covers in this scope are intact and ready to export.";

    partial void OnIncludeUnrankedChanged(bool value) => Recompute();

    private void Recompute()
    {
        var layout = BoardPngExporter.Layout(_board, IncludeUnranked);
        var pixels = (long)layout.Width * layout.Height;
        ExceedsPixelLimit = pixels > BoardPngExporter.MaxPixels;
        DimensionsText = $"{layout.Width.ToString("N0", CultureInfo.InvariantCulture)} × {layout.Height.ToString("N0", CultureInfo.InvariantCulture)} px";

        MissingOrBrokenInScope.Clear();
        foreach (var cell in layout.Cells)
        {
            var label = cell.CopyNumber > 1 ? $"{cell.Title} #{cell.CopyNumber} [{cell.Item.Value}]" : $"{cell.Title} [{cell.Item.Value}]";
            if (cell.Cover is not { } hash || !_assets.ContainsKey(hash))
            {
                MissingOrBrokenInScope.Add($"{label} — no cover");
            }
            else if (!_intactAssets.Contains(hash))
            {
                MissingOrBrokenInScope.Add($"{label} — damaged or missing cover file");
            }
        }

        OnPropertyChanged(nameof(HasBlockers));
        OnPropertyChanged(nameof(CanOpenUpdateBoard));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(BlockerSummary));
    }
}
