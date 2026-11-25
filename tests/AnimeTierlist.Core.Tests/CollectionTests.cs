using AnimeTierlist.Core;
using static AnimeTierlist.Core.Tests.TestData;

namespace AnimeTierlist.Core.Tests;

/// <summary>Curation, refresh, and title resolution.</summary>
public sealed class CollectionTests
{
    /// <summary>Builds the starting state: manual order C, A, X with C and A included, X excluded.</summary>
    private static (CollectionState Collection, SourceSnapshot Original) ManualCax()
    {
        var original = SeasonSnapshot(A, C, X);
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", original);
        collection = CollectionOperations.Move(collection, C, 0);
        collection = CollectionOperations.SetIncluded(collection, [X], included: false);
        Assert.Equal(["C", "A", "X"], Names(collection));
        return (collection, original);
    }

    [Fact]
    public void Initial_entries_start_included_in_source_order()
    {
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", SeasonSnapshot(B, A, C));

        Assert.Equal(["B", "A", "C"], Names(collection));
        Assert.All(collection.Entries, e => Assert.True(e.Included));
    }

    [Fact]
    public void Default_refresh_keeps_manual_order_appends_new_items_excluded_and_flags_missing()
    {
        var (collection, _) = ManualCax();
        var refreshed = SeasonSnapshot(A, B, C);

        var result = CollectionOperations.ApplyRefresh(collection, refreshed, adoptSourceOrder: false);

        Assert.Equal(["C", "A", "X", "B"], Names(result));
        Assert.False(result.Entry(B).Included);
        Assert.True(result.Entry(X).SourceMissing);
        Assert.False(result.Entry(X).Included);
        Assert.True(result.Entry(C).Included);
        Assert.True(result.Entry(A).Included);
        Assert.Equal(refreshed.Id, result.AppliedSnapshot);
    }

    [Fact]
    public void Refresh_with_adoption_follows_source_then_missing_items_in_prior_order()
    {
        var (collection, _) = ManualCax();

        var result = CollectionOperations.ApplyRefresh(collection, SeasonSnapshot(A, B, C), adoptSourceOrder: true);

        Assert.Equal(["A", "B", "C", "X"], Names(result));
        Assert.True(result.Entry(X).SourceMissing);
        Assert.Equal([true, false, true, false], result.Entries.Select(e => e.Included));
    }

    [Fact]
    public void Missing_entry_keeps_its_last_accepted_observation()
    {
        var (collection, original) = ManualCax();
        var acceptedBefore = collection.Entry(X).AcceptedOccurrence;

        var result = CollectionOperations.ApplyRefresh(collection, SeasonSnapshot(A, B, C), adoptSourceOrder: false);

        Assert.Equal(acceptedBefore, result.Entry(X).AcceptedOccurrence);
        Assert.Equal(original.Id, result.Entry(X).AcceptedOccurrence.Snapshot);
    }

    [Fact]
    public void Reappearing_entry_clears_missing_flag_and_binds_new_first_occurrence_without_reordering()
    {
        var (collection, _) = ManualCax();
        collection = CollectionOperations.ApplyRefresh(collection, SeasonSnapshot(A, B, C), adoptSourceOrder: false);
        var later = SeasonSnapshot(X, A, B, C);

        var result = CollectionOperations.ApplyRefresh(collection, later, adoptSourceOrder: false);

        Assert.Equal(["C", "A", "X", "B"], Names(result));
        Assert.False(result.Entry(X).SourceMissing);
        Assert.False(result.Entry(X).Included);
        Assert.Equal(new OccurrenceRef(later.Id, new SeasonKey(2025, Season.Summer), 0), result.Entry(X).AcceptedOccurrence);
    }

    [Fact]
    public void Refresh_preserves_overrides_and_selected_covers()
    {
        var (collection, _) = ManualCax();
        collection = CollectionOperations.SetTitleOverride(collection, A, "My title");
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(collection.Entry(A).AcceptedOccurrence, 'a'), out collection));

        var result = CollectionOperations.ApplyRefresh(collection, SeasonSnapshot(A, B, C), adoptSourceOrder: true);

        Assert.Equal("My title", result.Entry(A).TitleOverride);
        Assert.Equal(Hash('a'), result.Entry(A).Cover!.Asset);
    }

    [Fact]
    public void Preview_reports_additions_missing_reappeared_and_title_changes()
    {
        var original = SeasonSnapshotWithTitles((A, "Old A"), (C, null), (X, null));
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", original);
        var candidate = SeasonSnapshotWithTitles((A, "New A"), (B, null), (C, null));
        var snapshots = new[] { original, candidate }.ToDictionary(s => s.Id);

        var preview = CollectionOperations.PreviewRefresh(collection, candidate, r => snapshots[r.Snapshot].Occurrence(r));

        Assert.Equal(["B"], Names(preview.Added));
        Assert.Equal(["X"], Names(preview.NewlyMissing));
        Assert.Empty(preview.Reappeared);
        var change = Assert.Single(preview.TitleChanges);
        Assert.Equal(("Old A", "New A"), (change.Before.Displayed, change.After.Displayed));
    }

    [Fact]
    public void Preview_reports_a_newly_listed_cover_image_even_when_nothing_else_changed()
    {
        static SourceSnapshot Snapshot(bool withImage)
        {
            var occurrence = Occ(0, A);
            var capture = new SeasonCapture(new SeasonKey(2025, Season.Summer), CatalogueCategory.Tv, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "test-1",
                [withImage ? occurrence : occurrence with { ImageCandidates = [] }]);
            return new SourceSnapshot(SnapshotId.New(), new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv), [capture]);
        }

        var original = Snapshot(withImage: false);
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", original);
        var candidate = Snapshot(withImage: true);
        var snapshots = new[] { original, candidate }.ToDictionary(s => s.Id);

        var preview = CollectionOperations.PreviewRefresh(collection, candidate, r => snapshots[r.Snapshot].Occurrence(r));
        var unchanged = CollectionOperations.PreviewRefresh(collection, original, r => snapshots[r.Snapshot].Occurrence(r));

        Assert.Equal(["A"], Names(preview.CoverCandidateChanges));
        Assert.False(preview.IsEmpty);
        Assert.True(unchanged.IsEmpty);
        var applied = CollectionOperations.ApplyRefresh(collection, candidate, adoptSourceOrder: false);
        Assert.Equal(candidate.Id, applied.Entry(A).AcceptedOccurrence.Snapshot);
    }

    [Fact]
    public void Refresh_from_another_context_is_rejected()
    {
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", SeasonSnapshot(A));
        var other = new SourceSnapshot(SnapshotId.New(), new AcquisitionContext(2025, Season.Fall, CatalogueCategory.Tv), [Capture(2025, Season.Fall, A)]);

        Assert.Throws<InvalidOperationException>(() => CollectionOperations.ApplyRefresh(collection, other, adoptSourceOrder: false));
    }

    [Fact]
    public void Stale_download_does_not_attach_to_a_different_accepted_observation()
    {
        var original = SeasonSnapshot(A, B);
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", original);
        var staleOccurrence = collection.Entry(A).AcceptedOccurrence;
        collection = CollectionOperations.ApplyRefresh(collection, SeasonSnapshot(B, A), adoptSourceOrder: false);

        Assert.False(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(staleOccurrence, 'a'), out var unchanged));
        Assert.Same(collection, unchanged);
        Assert.Null(collection.Entry(A).Cover);
    }

    [Fact]
    public void Download_never_replaces_an_existing_selection()
    {
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", SeasonSnapshot(A));
        var occurrence = collection.Entry(A).AcceptedOccurrence;
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(occurrence, 'a'), out collection));

        Assert.False(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(occurrence, 'b'), out _));
    }

    [Fact]
    public void Recovery_cannot_replace_a_valid_cover()
    {
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", SeasonSnapshot(A));
        var occurrence = collection.Entry(A).AcceptedOccurrence;
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(occurrence, 'a'), out collection));

        Assert.Throws<InvalidOperationException>(() => CollectionOperations.ReplaceInvalidCover(collection, A, Cover(occurrence, 'b'), _ => true));
        var recovered = CollectionOperations.ReplaceInvalidCover(collection, A, Cover(occurrence, 'b'), _ => false);
        Assert.Equal(Hash('b'), recovered.Entry(A).Cover!.Asset);
    }

    [Fact]
    public void Two_collections_sharing_an_item_keep_their_own_observed_titles()
    {
        var first = SeasonSnapshotWithTitles((A, "X"));
        var second = SeasonSnapshotWithTitles((A, "Y"));
        var c1 = CollectionOperations.Create(CollectionId.New(), "One", first);
        var c2 = CollectionOperations.Create(CollectionId.New(), "Two", second);

        Assert.Equal("X", first.Occurrence(c1.Entry(A).AcceptedOccurrence).Titles.Displayed);
        Assert.Equal("Y", second.Occurrence(c2.Entry(A).AcceptedOccurrence).Titles.Displayed);
    }

    [Theory]
    [InlineData(TitleLanguage.SourceDefault, null, "Shown", TitleSource.SourceDefault, false)]
    [InlineData(TitleLanguage.Romanized, null, "Romaji", TitleSource.SelectedLanguage, false)]
    [InlineData(TitleLanguage.English, null, "Shown", TitleSource.SourceDefault, true)]
    [InlineData(TitleLanguage.Native, "Mine", "Mine", TitleSource.UserOverride, false)]
    public void Titles_resolve_override_then_language_then_source_default(TitleLanguage language, string? userOverride, string expected, TitleSource source, bool fallback)
    {
        var observed = new SourceTitles("Shown", english: null, romanized: "Romaji", native: "Native");

        var resolved = TitleResolver.Resolve(observed, language, userOverride);

        Assert.Equal(new ResolvedTitle(expected, source, fallback), resolved);
    }

    [Fact]
    public void Changing_language_does_not_change_identity_or_order()
    {
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer", SeasonSnapshot(C, A, B));

        var changed = CollectionOperations.SetLanguage(collection, TitleLanguage.Native);

        Assert.Equal(Names(collection), Names(changed));
        Assert.Equal(TitleLanguage.Native, changed.Language);
    }
}
