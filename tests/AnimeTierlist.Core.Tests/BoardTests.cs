using System.Collections.Immutable;
using AnimeTierlist.Core;
using static AnimeTierlist.Core.Tests.TestData;

namespace AnimeTierlist.Core.Tests;

/// <summary>Boards, tiers, placements, membership and presentation updates.</summary>
public sealed class BoardTests
{
    private static ResolvedTitle Title(CollectionEntry entry) => new($"Pinned {Name(entry.Item)}", TitleSource.SourceDefault, false);

    private static BoardState NewBoard(CollectionState collection, Func<AssetHash, bool>? valid = null) =>
        BoardOperations.Create(BoardId.New(), "Board", collection, Title, valid ?? (_ => true), BoardOperations.DefaultTiers());

    private static CollectionState Included(params LiveChartId[] ids) =>
        CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "c", SeasonSnapshot(ids)), true);

    [Fact]
    public void New_board_has_included_entries_unranked_in_collection_order()
    {
        var collection = CollectionOperations.SetIncluded(CollectionOperations.Create(CollectionId.New(), "c", SeasonSnapshot(C, A, B)), [A], false);

        var board = NewBoard(collection);

        Assert.Equal(["C", "B"], Names(board.Unranked));
        Assert.All(board.Tiers, t => Assert.Empty(board.Placements[t.Id]));
    }

    [Fact]
    public void Membership_update_appends_additions_and_unranked_follows_seed_order()
    {
        // Seeds A, C with A ranked; collection becomes C, B, A; explicitly add B.
        var original = SeasonSnapshot(A, C);
        var collection = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "c", original), true);
        var board = NewBoard(collection);
        var top = board.Tiers[0].Id;
        board = BoardOperations.MoveToTier(board, A, top, 0);

        collection = CollectionOperations.ApplyRefresh(collection, SeasonSnapshot(C, B, A), adoptSourceOrder: true);
        collection = CollectionOperations.SetIncluded(collection, [B], true);
        var proposal = BoardOperations.ProposeUpdate(board, collection);
        Assert.Equal(["B"], Names(proposal.AdditionCandidates));
        Assert.Empty(proposal.RemovalCandidates);

        var updated = BoardOperations.ApplyUpdate(board, collection, proposal.AdditionCandidates, [], PresentationUpdate.None, Title, _ => true);

        Assert.Equal(["A", "C", "B"], Names(updated.Entries.Select(e => e.Item)));
        Assert.Equal(["A"], Names(updated.Placements[top]));
        Assert.Equal(["C", "B"], Names(updated.Unranked));

        var returned = BoardOperations.ReturnToUnranked(updated, A);
        Assert.Equal(["A", "C", "B"], Names(returned.Unranked));
    }

    [Fact]
    public void Excluding_from_collection_does_not_remove_board_entry_until_reviewed_removal()
    {
        var collection = Included(A, B);
        var board = NewBoard(collection);
        collection = CollectionOperations.SetIncluded(collection, [B], false);

        Assert.True(board.Contains(B));
        var proposal = BoardOperations.ProposeUpdate(board, collection);
        Assert.Equal(["B"], Names(proposal.RemovalCandidates));

        var addOnly = BoardOperations.ApplyUpdate(board, collection, [], [], PresentationUpdate.None, Title, _ => true);
        Assert.True(addOnly.Contains(B));

        var removed = BoardOperations.ApplyUpdate(board, collection, [], [B], PresentationUpdate.None, Title, _ => true);
        Assert.False(removed.Contains(B));
    }

    [Fact]
    public void Update_rejects_items_that_were_not_reviewed_candidates()
    {
        var collection = Included(A, B);
        var board = NewBoard(collection);

        Assert.Throws<InvalidOperationException>(() => BoardOperations.ApplyUpdate(board, collection, [], [A], PresentationUpdate.None, Title, _ => true));
    }

    [Fact]
    public void Moving_between_tiers_removes_preceding_placement()
    {
        var board = NewBoard(Included(A, B, C));
        var (s, a) = (board.Tiers[0].Id, board.Tiers[1].Id);

        board = BoardOperations.MoveToTier(board, B, s, 0);
        board = BoardOperations.MoveToTier(board, C, s, 0);
        board = BoardOperations.MoveToTier(board, B, a, 0);

        Assert.Equal(["C"], Names(board.Placements[s]));
        Assert.Equal(["B"], Names(board.Placements[a]));
        Assert.Equal(["A"], Names(board.Unranked));
    }

    [Fact]
    public void Deleting_nonempty_tier_returns_entries_to_unranked_in_seed_order()
    {
        var board = NewBoard(Included(A, B, C, D));
        var s = board.Tiers[0].Id;
        board = BoardOperations.MoveToTier(board, D, s, 0);
        board = BoardOperations.MoveToTier(board, B, s, 0);

        var deleted = BoardOperations.DeleteTier(board, s);

        Assert.Equal(4, deleted.Tiers.Length);
        Assert.Equal(["A", "B", "C", "D"], Names(deleted.Unranked));
    }

    [Fact]
    public void Tiers_can_be_added_edited_and_reordered()
    {
        var board = NewBoard(Included(A));
        var extra = new Tier(TierId.New(), "F", new TierColor(0x7F, 0x7F, 0xFF));

        board = BoardOperations.AddTier(board, extra, board.Tiers.Length);
        board = BoardOperations.EditTier(board, extra.Id, "Never again", new TierColor(0, 0, 0));
        board = BoardOperations.MoveTier(board, extra.Id, 0);

        Assert.Equal("Never again", board.Tiers[0].Name);
        Assert.Equal(6, board.Tiers.Length);
    }

    [Fact]
    public void Collection_changes_do_not_alter_pinned_presentation_until_explicit_update()
    {
        var collection = Included(A);
        var board = NewBoard(collection);
        collection = CollectionOperations.SetTitleOverride(collection, A, "Renamed");

        Assert.Equal("Pinned A", board.Entries[0].PinnedTitle);
        var refreshed = BoardOperations.UpdatePresentation(board, collection, PresentationUpdate.FullRefresh,
            e => TitleResolver.Resolve(new SourceTitles("Shown", null, null, null), TitleLanguage.SourceDefault, e.TitleOverride), _ => true);
        Assert.Equal("Renamed", refreshed.Entries[0].PinnedTitle);
    }

    [Fact]
    public void Fill_missing_covers_only_changes_absent_or_invalid_pins()
    {
        var collection = Included(A, B, C);
        // C has a valid cover at creation; A and B are still downloading.
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, C, Cover(collection.Entry(C).AcceptedOccurrence, 'c'), out collection));
        var board = NewBoard(collection);
        Assert.Null(board.Entries[0].PinnedCover);

        // A's download completes on the collection; the board pin stays missing.
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(collection.Entry(A).AcceptedOccurrence, 'a'), out collection));
        Assert.Null(board.Entries[0].PinnedCover);

        // C's collection cover is later replaced through recovery; fill-only must keep the valid pin 'c'.
        collection = CollectionOperations.MarkCoverMissing(collection, C);
        collection = CollectionOperations.ReplaceInvalidCover(collection, C, Cover(collection.Entry(C).AcceptedOccurrence, 'd'), _ => false);

        var filled = BoardOperations.UpdatePresentation(board, collection, PresentationUpdate.FillMissingCovers, _ => new ResolvedTitle("changed", TitleSource.UserOverride, false), _ => true);

        Assert.Equal(Hash('a'), filled.Entries[0].PinnedCover);
        Assert.Null(filled.Entries[1].PinnedCover);
        Assert.Equal(Hash('c'), filled.Entries[2].PinnedCover);
        Assert.All(filled.Entries, e => Assert.StartsWith("Pinned", e.PinnedTitle, StringComparison.Ordinal));
    }

    [Fact]
    public void Fill_clears_invalid_pin_to_missing_when_no_valid_collection_cover_exists()
    {
        var collection = Included(A);
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, A, Cover(collection.Entry(A).AcceptedOccurrence, 'a'), out collection));
        var board = NewBoard(collection);
        Assert.Equal(Hash('a'), board.Entries[0].PinnedCover);

        var filled = BoardOperations.UpdatePresentation(board, collection, PresentationUpdate.FillMissingCovers, Title, _ => false);

        Assert.Null(filled.Entries[0].PinnedCover);
        Assert.Equal("Pinned A", filled.Entries[0].PinnedTitle);
    }

    [Fact]
    public void Boards_from_one_collection_are_independent()
    {
        var collection = Included(A, B);
        var first = NewBoard(collection);
        var second = NewBoard(collection);

        first = BoardOperations.MoveToTier(first, A, first.Tiers[0].Id, 0);

        Assert.Equal(["A", "B"], Names(second.Unranked));
    }

    [Fact]
    public void Invalid_states_are_rejected()
    {
        var tier = new Tier(TierId.New(), "S", default);
        var entries = ImmutableArray.Create(new BoardEntry(A, "A", null));
        var keyA = new BoardEntryKey(A);
        var keyB = new BoardEntryKey(B);
        var twice = ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>.Empty.Add(tier.Id, [keyA, keyA]);
        var stranger = ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>.Empty.Add(tier.Id, [keyB]);

        Assert.Throws<ArgumentException>(() => new BoardState(BoardId.New(), CollectionId.New(), "b", [tier], entries, twice));
        Assert.Throws<ArgumentException>(() => new BoardState(BoardId.New(), CollectionId.New(), "b", [tier], entries, stranger));
    }

    [Fact]
    public void Duplicating_an_entry_inserts_numbered_copies_next_to_original_in_unranked()
    {
        var board = NewBoard(Included(A, B, C));

        board = BoardOperations.DuplicateEntry(board, A, out var copy2);
        board = BoardOperations.DuplicateEntry(board, A, out var copy3);
        board = BoardOperations.DuplicateEntry(board, B, out var copyB2);

        Assert.Equal(2, copy2.CopyNumber);
        Assert.Equal(3, copy3.CopyNumber);
        Assert.Equal(2, copyB2.CopyNumber);
        Assert.Equal(["A", "A#2", "A#3", "B", "B#2", "C"], Names(board.Unranked));

        // Copies move between tiers independently of the original.
        var s = board.Tiers[0].Id;
        var a = board.Tiers[1].Id;
        board = BoardOperations.MoveToTier(board, new BoardEntryKey(A, 1), s, 0);
        board = BoardOperations.MoveToTier(board, new BoardEntryKey(A, 2), a, 0);

        Assert.Equal(["A"], Names(board.Placements[s]));
        Assert.Equal(["A#2"], Names(board.Placements[a]));
        Assert.Equal(["A#3", "B", "B#2", "C"], Names(board.Unranked));

        // Returning the original restores it right before its unranked copies.
        board = BoardOperations.ReturnToUnranked(board, new BoardEntryKey(A, 1));
        Assert.Equal(["A", "A#3", "B", "B#2", "C"], Names(board.Unranked));
    }

    [Fact]
    public void Removing_a_duplicate_keeps_original_and_reuses_the_freed_copy_number_in_order()
    {
        var board = NewBoard(Included(A, B));
        board = BoardOperations.DuplicateEntry(board, A);
        board = BoardOperations.DuplicateEntry(board, A);
        board = BoardOperations.MoveToTier(board, new BoardEntryKey(A, 2), board.Tiers[0].Id, 0);

        // Original copy cannot be removed via RemoveDuplicate.
        Assert.Throws<InvalidOperationException>(() => BoardOperations.RemoveDuplicate(board, new BoardEntryKey(A, 1)));

        board = BoardOperations.RemoveDuplicate(board, new BoardEntryKey(A, 2));
        Assert.Empty(board.Placements[board.Tiers[0].Id]);
        Assert.Equal(["A", "A#3", "B"], Names(board.Unranked));

        // Next duplication fills copy #2 right between #1 and #3.
        board = BoardOperations.DuplicateEntry(board, A, out var recreated);
        Assert.Equal(2, recreated.CopyNumber);
        Assert.Equal(["A", "A#2", "A#3", "B"], Names(board.Unranked));
    }

    [Fact]
    public void Board_updates_refresh_and_remove_all_copies_of_an_item_together()
    {
        var collection = Included(A, B);
        var board = NewBoard(collection);
        board = BoardOperations.DuplicateEntry(board, A);
        board = BoardOperations.DuplicateEntry(board, B);
        board = BoardOperations.MoveToTier(board, new BoardEntryKey(B, 2), board.Tiers[0].Id, 0);

        collection = CollectionOperations.SetTitleOverride(collection, A, "Updated A");
        collection = CollectionOperations.SetIncluded(collection, [B], false);

        var proposal = BoardOperations.ProposeUpdate(board, collection);
        Assert.Equal(["B"], Names(proposal.RemovalCandidates));

        var updated = BoardOperations.ApplyUpdate(
            board,
            collection,
            [],
            [B],
            PresentationUpdate.FullRefresh,
            e => TitleResolver.Resolve(new SourceTitles("Orig", null, null, null), TitleLanguage.SourceDefault, e.TitleOverride),
            _ => true);

        Assert.Equal(["A", "A#2"], Names(updated.Unranked));
        Assert.All(updated.Entries, e => Assert.Equal("Updated A", e.PinnedTitle));
        Assert.Empty(updated.Placements[updated.Tiers[0].Id]);
    }

    [Fact]
    public void Randomized_move_sequences_preserve_exactly_one_placement_per_entry()
    {
        var ids = Enumerable.Range(1, 40).Select(i => new LiveChartId(i)).ToArray();
        var board = NewBoard(Included(ids));
        var random = new Random(20261117);

        for (var step = 0; step < 2000; step++)
        {
            var item = ids[random.Next(ids.Length)];
            switch (random.Next(10))
            {
                case 0:
                    board = BoardOperations.ReturnToUnranked(board, item);
                    break;
                case 1 when board.Tiers.Length > 1:
                    board = BoardOperations.DeleteTier(board, board.Tiers[random.Next(board.Tiers.Length)].Id);
                    break;
                case 2:
                    board = BoardOperations.AddTier(board, new Tier(TierId.New(), $"T{step}", default), random.Next(board.Tiers.Length + 1));
                    break;
                default:
                    var tier = board.Tiers[random.Next(board.Tiers.Length)].Id;
                    var length = board.Placements[tier].Length - (board.TierOf(item) == tier ? 1 : 0);
                    board = BoardOperations.MoveToTier(board, item, tier, random.Next(length + 1));
                    break;
            }

            var placed = board.Placements.Values.SelectMany(v => v).Concat(board.Unranked).ToList();
            Assert.Equal(ids.Length, placed.Count);
            Assert.Equal(ids.Select(i => new BoardEntryKey(i)).ToHashSet(), placed.ToHashSet());
        }

        Assert.Equal(ids.Select(i => i.Value), board.Entries.Select(e => e.Item.Value));
    }

    [Fact]
    public void Tier_color_preferred_ink_achieves_at_least_wcag_aa_contrast_for_every_srgb_color()
    {
        var black = new TierColor(0x00, 0x00, 0x00);
        var white = new TierColor(0xFF, 0xFF, 0xFF);
        var samples = BoardOperations.DefaultTiers().Select(t => t.Color).Concat(
        [
            new TierColor(0x00, 0x00, 0x00),
            new TierColor(0xFF, 0xFF, 0xFF),
            new TierColor(0xDC, 0x26, 0x26),
            new TierColor(0xD9, 0x77, 0x06),
            new TierColor(0x05, 0x96, 0x69),
            new TierColor(0x25, 0x63, 0xEB),
            new TierColor(0x7C, 0x3A, 0xED),
            new TierColor(0x47, 0x55, 0x69),
            new TierColor(0x76, 0x76, 0x76),
        ]);

        foreach (var color in samples)
        {
            var ink = color.PreferredInk() == TierInk.White ? white : black;
            var ratio = color.ContrastRatio(ink);
            Assert.True(ratio >= 4.5, $"PreferredInk for {color.ToHex()} yielded contrast {ratio:F2}:1 (< 4.5:1).");
        }

        Assert.All(BoardOperations.DefaultTiers(), t => Assert.Equal(TierInk.Black, t.Color.PreferredInk()));
        Assert.Equal(TierInk.White, new TierColor(0x25, 0x63, 0xEB).PreferredInk());
    }
}
