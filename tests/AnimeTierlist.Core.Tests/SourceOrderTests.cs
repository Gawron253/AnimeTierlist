using System.Collections.Immutable;
using AnimeTierlist.Core;
using static AnimeTierlist.Core.Tests.TestData;

namespace AnimeTierlist.Core.Tests;

/// <summary>Source sequence, calendar-year composition, and repeated-ID policy.</summary>
public sealed class SourceOrderTests
{
    [Fact]
    public void Sequence_follows_source_ordinals_not_ids_or_titles()
    {
        var snapshot = SeasonSnapshotWithTitles((C, "aaa"), (A, "zzz"), (B, "aaa"));

        Assert.Equal(["C", "A", "B"], Names(snapshot.Sequence.Select(s => s.Item)));
    }

    [Fact]
    public void Year_combines_winter_spring_summer_fall_regardless_of_capture_order()
    {
        var context = new AcquisitionContext(2025, null, CatalogueCategory.Tv);
        var fall = Capture(2025, Season.Fall, D);
        var winter = Capture(2025, Season.Winter, B, A);
        var summer = Capture(2025, Season.Summer, C);
        var spring = Capture(2025, Season.Spring, X);

        var snapshot = new SourceSnapshot(SnapshotId.New(), context, [fall, summer, winter, spring]);

        Assert.Equal(["B", "A", "X", "C", "D"], Names(snapshot.Sequence.Select(s => s.Item)));
        Assert.Equal([Season.Winter, Season.Spring, Season.Summer, Season.Fall], snapshot.Captures.Select(c => c.Season.Season));
    }

    [Fact]
    public void Year_without_all_four_seasons_is_not_a_snapshot()
    {
        var context = new AcquisitionContext(2025, null, CatalogueCategory.Tv);

        Assert.Throws<ArgumentException>(() => new SourceSnapshot(SnapshotId.New(), context, [Capture(2025, Season.Winter, A), Capture(2025, Season.Spring, B), Capture(2025, Season.Summer, C)]));
    }

    [Fact]
    public void Repeated_ids_use_first_calendar_occurrence_and_keep_every_occurrence()
    {
        var context = new AcquisitionContext(2025, null, CatalogueCategory.Tv);
        var snapshot = new SourceSnapshot(SnapshotId.New(), context,
        [
            Capture(2025, Season.Winter, A, B),
            Capture(2025, Season.Spring, C, A),
            Capture(2025, Season.Summer, B, D),
            Capture(2025, Season.Fall, A),
        ]);

        Assert.Equal(["A", "B", "C", "D"], Names(snapshot.Sequence.Select(s => s.Item)));
        Assert.Equal(7, snapshot.OccurrenceCount);
        Assert.True(snapshot.TryGetFirstOccurrence(A, out var first));
        Assert.Equal(new OccurrenceRef(snapshot.Id, new SeasonKey(2025, Season.Winter), 0), first);
    }

    [Fact]
    public void Repeated_id_within_one_season_keeps_both_occurrences()
    {
        var snapshot = SeasonSnapshot(A, B, A);

        Assert.Equal(["A", "B"], Names(snapshot.Sequence.Select(s => s.Item)));
        Assert.Equal(3, snapshot.Captures[0].Occurrences.Length);
    }

    [Fact]
    public void Ordinals_must_be_contiguous()
    {
        var gap = new[] { Occ(0, A), Occ(2, B) }.ToImmutableArray();

        Assert.Throws<ArgumentException>(() => new SeasonCapture(new SeasonKey(2025, Season.Summer), CatalogueCategory.Tv, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "p", gap));
    }

    [Fact]
    public void Selection_order_does_not_define_collection_order()
    {
        // A, B, C captured; the user excludes everything, then selects C, then A.
        var collection = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Summer", SeasonSnapshot(A, B, C)), false);
        collection = CollectionOperations.SetIncluded(collection, [C], included: true);
        collection = CollectionOperations.SetIncluded(collection, [A], included: true);

        Assert.Equal(["A", "C"], Names(collection.IncludedEntries.Select(e => e.Item)));
    }

    [Fact]
    public void Download_completion_order_does_not_change_collection_order()
    {
        var snapshot = SeasonSnapshot(A, B, C);
        var collection = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Summer", snapshot), true);

        foreach (var (item, hash) in new[] { (C, 'c'), (A, 'a'), (B, 'b') })
        {
            Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, item, Cover(collection.Entry(item).AcceptedOccurrence, hash), out collection));
        }

        Assert.Equal(["A", "B", "C"], Names(collection));
    }

    [Fact]
    public void Identical_titles_never_merge_distinct_ids()
    {
        var snapshot = SeasonSnapshotWithTitles((A, "Same"), (B, "Same"));

        Assert.Equal(2, CollectionOperations.Create(CollectionId.New(), "c", snapshot).Entries.Length);
    }
}
