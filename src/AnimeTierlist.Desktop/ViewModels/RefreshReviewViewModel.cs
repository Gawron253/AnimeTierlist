using AnimeTierlist.Application.Session;
using AnimeTierlist.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimeTierlist.Desktop.ViewModels;

/// <summary>Structured before/after title change entry for the refresh review diff view.</summary>
public sealed record TitleChangeRow(string Before, string After);

/// <summary>The reviewable difference between a collection and LiveChart's latest snapshot for the same context.</summary>
public sealed partial class RefreshReviewViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepManualOrder))]
    private bool _adoptSourceOrder;

    /// <summary>Creates a reviewable refresh preview comparing a collection against a candidate snapshot.</summary>
    public RefreshReviewViewModel(CollectionState collection, SourceSnapshot candidate, RefreshPreview preview, ProjectState state)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(state);
        CollectionName = collection.Name;
        string Current(LiveChartId item) => state.ResolveTitle(collection, collection.Entry(item)).Text;
        string Candidate(LiveChartId item) => candidate.TryGetFirstOccurrence(item, out var first) ? candidate.Occurrence(first).Titles.Displayed : item.ToString();

        Added = preview.Added.Select(Candidate).ToList();
        NoLongerListed = preview.NewlyMissing.Select(Current).ToList();
        Reappeared = preview.Reappeared.Select(Current).ToList();
        TitleChanges = preview.TitleChanges.Select(c => $"{c.Before.Displayed}  →  {c.After.Displayed}").ToList();
        TitleChangeRows = preview.TitleChanges.Select(c => new TitleChangeRow(c.Before.Displayed, c.After.Displayed)).ToList();
        SourceOrderDiffers = preview.SourceOrderDiffers;
        CoverCandidateChanges = preview.CoverCandidateChanges.Length;
    }

    /// <summary>Inverse binding helper for radio buttons choosing between keeping manual order and adopting source order.</summary>
    public bool KeepManualOrder
    {
        get => !AdoptSourceOrder;
        set
        {
            if (value)
            {
                AdoptSourceOrder = false;
            }
        }
    }

    /// <summary>Entries whose LiveChart image links changed. Only entries without a cover use them, through an explicit cover fetch after applying.</summary>
    public int CoverCandidateChanges { get; }

    public string CollectionName { get; }

    /// <summary>New shows. They append after existing entries (or follow LiveChart's order when adopted) and start excluded.</summary>
    public IReadOnlyList<string> Added { get; }

    /// <summary>Shows LiveChart no longer lists. They stay in the collection with their inclusion, flagged as no longer listed.</summary>
    public IReadOnlyList<string> NoLongerListed { get; }

    public IReadOnlyList<string> Reappeared { get; }

    /// <summary>LiveChart title changes. Custom titles and existing boards are not affected.</summary>
    public IReadOnlyList<string> TitleChanges { get; }

    /// <summary>Structured two-column before/after rows for clean diff rendering.</summary>
    public IReadOnlyList<TitleChangeRow> TitleChangeRows { get; }

    public bool SourceOrderDiffers { get; }

    public string Heading => $"Update '{CollectionName}' from LiveChart";
}
