using System.IO;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.ViewModels;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Board workflows through the real view models and session: creation, keyboard-equivalent moves, drag
/// drops, tier edits, reviewed updates, deletion, undo, and reopen. Commands are those bound to keys and buttons.
/// </summary>
public sealed class BoardWorkflowTests
{
    [Fact]
    public Task New_board_starts_with_included_shows_unranked_in_collection_order() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var included = new[] { collection.Entries[4], collection.Entries[1], collection.Entries[2] };

        var board = await CreateBoardAsync(harness, collection, "Favourites", included);

        Assert.Equal(collection.Entries.Where(included.Contains).Select(e => e.Item), board.Unranked.Entries.Select(e => e.Item));
        Assert.Equal(["S", "A", "B", "C", "D"], board.Tiers.Select(t => t.Name));
        Assert.All(board.Tiers, t => Assert.Empty(t.Entries));
        Assert.Equal("Favourites", Assert.Single(harness.Project.Boards).Label);
        await Ui.UntilAsync(() => harness.Project.UndoLabel == "Create board 'Favourites'", "undo label");
    });

    [Fact]
    public Task Keyboard_moves_place_reorder_and_return_shows_and_survive_reopen() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Ranked", collection.Entries.Take(4).ToArray());
        var seeds = board.Unranked.Entries.Select(e => e.Item).ToList();
        var (a, b, c) = (board.Unranked.Entries[0], board.Unranked.Entries[1], board.Unranked.Entries[2]);

        Select(board, a);
        board.MoveToTierCommand.Execute("1");     // key 1: A to S
        Select(board, b);
        board.MoveToTierCommand.Execute("1");     // key 1: B to S after A
        board.MoveLeftCommand.Execute(null);      // Ctrl+Left: B before A
        Select(board, c);
        board.MoveToTierCommand.Execute("2");     // key 2: C to A
        board.MoveToTierBelowCommand.Execute(null); // Alt+Down: C to B

        var state = harness.Session.Current.Board(board.Id);
        Assert.Equal([b.Key, a.Key], state.Placements[state.Tiers[0].Id]);
        Assert.Equal([c.Key], state.Placements[state.Tiers[2].Id]);
        Assert.Equal([b, a], board.Tiers[0].Entries);
        Assert.Same(c, board.SelectedEntry);
        Assert.Same(c, board.Tiers[2].SelectedEntry);

        Select(board, a);
        board.MoveToUnrankedCommand.Execute(null); // key 0: A back to its seed position
        Assert.Equal(seeds.Where(i => i != b.Item && i != c.Item), board.Unranked.Entries.Select(e => e.Item));

        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => board.Tiers[0].Entries.Count == 2, "undo restores A in S");
        Assert.Equal([b, a], board.Tiers[0].Entries);

        await harness.ReopenAsync();
        harness.Project.SelectedBoard = harness.Project.Boards.Single();
        var reopened = Assert.IsType<BoardViewModel>(harness.Project.Content);
        Assert.Equal([b.Item, a.Item], reopened.Tiers[0].Entries.Select(e => e.Item));
        Assert.Equal([c.Item], reopened.Tiers[2].Entries.Select(e => e.Item));
    });

    [Fact]
    public Task Drops_insert_at_the_pointer_position_and_into_the_pool() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Dragged", collection.Entries.Take(3).ToArray());
        var (a, b, c) = (board.Unranked.Entries[0], board.Unranked.Entries[1], board.Unranked.Entries[2]);
        var s = board.Tiers[0];

        board.Drop(a, s, 0);
        board.Drop(b, s, 0);   // before A
        board.Drop(c, s, 1);   // between B and A
        Assert.Equal([b, c, a], s.Entries);

        board.Drop(b, s, 3);   // within the same row, to the end
        Assert.Equal([c, a, b], s.Entries);

        board.Drop(c, board.Unranked, 0);
        Assert.Equal([a, b], s.Entries);
        Assert.Equal([c], board.Unranked.Entries);
    });

    [Fact]
    public Task Tiers_can_be_edited_added_reordered_and_deleted_with_confirmation() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Tiers", collection.Entries.Take(2).ToArray());
        var shown = board.Unranked.Entries[0];
        Select(board, shown);
        board.MoveToTierCommand.Execute("2");

        board.SelectedTier = board.Tiers[1];
        Assert.Equal("A", board.TierNameText);
        board.TierNameText = "Great";
        board.TierColor = board.ColorOptions.Single(o => o.Label == "Blue");
        board.EditTierCommand.Execute(null);
        Assert.Equal("Great", board.Tiers[1].Name);
        Assert.Equal(new TierColor(0x7F, 0xBF, 0xFF), harness.Session.Current.Board(board.Id).Tiers[1].Color);

        board.TierNameText = "Unsure";
        board.AddTierCommand.Execute(null);
        Assert.Equal(["S", "Great", "Unsure", "B", "C", "D"], board.Tiers.Select(t => t.Name));
        board.MoveTierUpCommand.Execute(null);
        Assert.Equal(["S", "Unsure", "Great", "B", "C", "D"], board.Tiers.Select(t => t.Name));

        board.SelectedTier = board.Tiers.Single(t => t.Name == "Great");
        harness.Dialogs.Answer = false;
        board.DeleteTierCommand.Execute(null);
        Assert.Contains(board.Tiers, t => t.Name == "Great");

        harness.Dialogs.Answer = true;
        board.DeleteTierCommand.Execute(null);
        Assert.DoesNotContain(board.Tiers, t => t.Name == "Great");
        Assert.Contains(shown, board.Unranked.Entries);
        Assert.Equal(["Delete tier", "Delete tier"], harness.Dialogs.Questions);

        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => board.Tiers.Any(t => t.Name == "Great" && t.Entries.Contains(shown)), "tier and placement restored");
    });

    [Fact]
    public Task Reviewed_update_adds_and_removes_and_declining_removal_cancels_everything() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Updated", collection.Entries.Take(2).ToArray());
        var kept = board.Unranked.Entries[0];
        var dropped = board.Unranked.Entries[1];
        Select(board, kept);
        board.MoveToTierCommand.Execute("1");
        collection.Entries[1].Included = false;
        collection.Entries[5].Included = true;
        var added = collection.Entries[5].Item;
        var before = harness.Session.Current.Board(board.Id);

        BoardUpdateViewModel? seen = null;
        harness.Dialogs.ReviewBoardUpdate = review => seen = review;
        harness.Dialogs.Answer = false;          // decline the removal confirmation
        await board.UpdateFromCollectionCommand.ExecuteAsync(null);

        Assert.Equal([added], seen!.Additions.Select(s => s.Item));
        Assert.Equal([dropped.Item], seen.Removals.Select(s => s.Item));
        Assert.Same(before, harness.Session.Current.Board(board.Id));

        harness.Dialogs.Answer = true;
        await board.UpdateFromCollectionCommand.ExecuteAsync(null);

        var after = harness.Session.Current.Board(board.Id);
        Assert.Equal([kept.Item, added], after.Entries.Select(e => e.Item));
        Assert.Equal([kept.Key], after.Placements[after.Tiers[0].Id]);
        Assert.Equal([added], board.Unranked.Entries.Select(e => e.Item));
        await Ui.UntilAsync(() => harness.Project.UndoLabel == "Update board", "one undoable update");

        harness.Project.UndoCommand.Execute(null);
        Assert.Equal(before, harness.Session.Current.Board(board.Id));
    });

    [Fact]
    public Task Covers_arriving_after_board_creation_fill_pins_only_through_the_reviewed_update() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Late covers", collection.Entries.Take(2).ToArray());
        Assert.All(harness.Session.Current.Board(board.Id).Entries, e => Assert.Null(e.PinnedCover));
        var titles = harness.Session.Current.Board(board.Id).Entries.Select(e => e.PinnedTitle).ToList();

        harness.ServeCovers();
        await collection.FetchCoversCommand.ExecuteAsync(null);
        Assert.All(harness.Collection(collection).IncludedEntries, e => Assert.NotNull(e.Cover));
        Assert.All(harness.Session.Current.Board(board.Id).Entries, e => Assert.Null(e.PinnedCover));

        harness.Dialogs.ReviewBoardUpdate = review =>
        {
            review.Presentation = review.PresentationOptions.Single(o => o.Value == PresentationUpdate.FillMissingCovers);
            Assert.Equal("2 show(s) change:", review.PresentationPreview);
            Assert.All(review.PresentationChanges, c => Assert.EndsWith(": missing cover filled from the collection", c, StringComparison.Ordinal));
        };
        await board.UpdateFromCollectionCommand.ExecuteAsync(null);

        var filled = harness.Session.Current.Board(board.Id);
        Assert.Equal(harness.Collection(collection).IncludedEntries.Select(e => e.Cover!.Asset), filled.Entries.Select(e => e.PinnedCover!.Value));
        Assert.Equal(titles, filled.Entries.Select(e => e.PinnedTitle));
        await Ui.UntilAsync(() => board.Unranked.Entries.All(e => e.HasCover), "board thumbnails shown");
    });

    [Fact]
    public Task Preview_lists_each_change_and_skips_shows_selected_for_removal() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Preview", collection.Entries.Take(2).ToArray());
        collection.SelectedEntry = collection.Entries[0];
        collection.TitleOverrideText = "Renamed A";
        collection.SetTitleOverrideCommand.Execute(null);
        collection.SelectedEntry = collection.Entries[1];
        collection.TitleOverrideText = "Renamed B";
        collection.SetTitleOverrideCommand.Execute(null);
        collection.Entries[1].Included = false;

        harness.Dialogs.ReviewBoardUpdate = review =>
        {
            review.Presentation = review.PresentationOptions.Single(o => o.Value == PresentationUpdate.FullRefresh);
            var line = Assert.Single(review.PresentationChanges);
            Assert.Contains("becomes “Renamed A”", line, StringComparison.Ordinal);

            review.Removals.Single().IsSelected = false;
            Assert.Equal(2, review.PresentationChanges.Count);
            review.Removals.Single().IsSelected = true;
        };
        harness.Dialogs.Answer = true;
        await board.UpdateFromCollectionCommand.ExecuteAsync(null);

        Assert.Equal(["Renamed A"], harness.Session.Current.Board(board.Id).Entries.Select(e => e.PinnedTitle));
    });

    [Fact]
    public Task An_update_is_rejected_when_the_collection_changes_during_review() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Raced", collection.Entries.Take(1).ToArray());
        collection.Entries[3].Included = true;
        var before = harness.Session.Current.Board(board.Id);

        harness.Dialogs.ReviewBoardUpdate = _ => collection.Entries[4].Included = true;
        await board.UpdateFromCollectionCommand.ExecuteAsync(null);

        Assert.Same(before, harness.Session.Current.Board(board.Id));
        Assert.Contains("changed during the review", harness.Project.Status, StringComparison.Ordinal);
    });

    [Fact]
    public Task An_update_is_rejected_when_a_cover_file_is_repaired_in_place_during_review() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers selected");
        var board = await CreateBoardAsync(harness, collection, "Repair race", collection.Entries.Take(1).ToArray());
        await Ui.UntilAsync(() => !board.IsCheckingCovers, "board cover check");
        var hash = harness.Session.Current.Board(board.Id).Entries[0].PinnedCover!.Value;
        var path = new AnimeTierlist.Infrastructure.Assets.AssetStore(harness.ProjectDirectory).PathOf(harness.Session.Current.Assets[hash]);
        var original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [1, 2, 3]);
        var before = harness.Session.Current.Board(board.Id);

        harness.Dialogs.ReviewBoardUpdate = review =>
        {
            review.Presentation = review.PresentationOptions.Single(o => o.Value == PresentationUpdate.FullRefresh);
            File.WriteAllBytes(path, original);   // repaired in place while the dialog is open
        };
        await board.UpdateFromCollectionCommand.ExecuteAsync(null);

        Assert.Same(before, harness.Session.Current.Board(board.Id));
        Assert.Contains("Cover files changed during the review", harness.Project.Status, StringComparison.Ordinal);
    });

    [Fact]
    public Task Undo_of_a_tier_edit_reloads_the_tier_editor_but_unrelated_refreshes_keep_drafts() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Editor", collection.Entries.Take(2).ToArray());
        board.SelectedTier = board.Tiers[0];
        board.TierNameText = "Top";
        board.TierColor = board.ColorOptions.Single(o => o.Label == "Blue");
        board.EditTierCommand.Execute(null);

        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => board.Tiers[0].Name == "S", "tier name restored");
        Assert.Equal("S", board.TierNameText);
        Assert.Equal("Red", board.TierColor.Label);

        board.TierNameText = "Draft";
        Select(board, board.Unranked.Entries[0]);
        board.MoveToTierCommand.Execute("2");
        Assert.Equal("Draft", board.TierNameText);
    });

    [Fact]
    public Task Deleting_a_board_asks_first_returns_to_its_collection_and_can_be_undone() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Temporary", collection.Entries.Take(1).ToArray());

        harness.Dialogs.Answer = false;
        board.DeleteCommand.Execute(null);
        Assert.Single(harness.Session.Current.Boards);

        harness.Dialogs.Answer = true;
        board.DeleteCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.Boards.Count == 0, "board removed from the list");
        Assert.Same(collection, harness.Project.Content);

        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.Boards.Count == 1, "board restored");
        Assert.Equal("Temporary", harness.Session.Current.Boards.Values.Single().Name);
    });

    [Fact]
    public Task Board_entry_details_shows_livechart_info_duplicates_next_to_original_and_survives_reopen() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: true);
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers loaded");
        var board = await CreateBoardAsync(harness, collection, "Openings", collection.Entries.Take(3).ToArray());

        var original = board.Unranked.Entries[0];
        var secondShow = board.Unranked.Entries[1];
        Assert.False(original.IsDuplicate);
        Assert.Equal(1, original.CopyNumber);

        // Open details via command and verify captured LiveChart info and duplicate action.
        harness.Dialogs.InspectEntryDetails = details =>
        {
            Assert.Equal(original.Title, details.Title);
            Assert.Equal($"LiveChart #{original.Item.Value}", details.LiveChartIdText);
            Assert.Equal($"https://www.livechart.me/anime/{original.Item.Value}", details.LiveChartUrl.AbsoluteUri);
            Assert.Contains("Popularity #1", details.SourceAppearanceText, StringComparison.Ordinal);
            Assert.False(details.IsDuplicate);
            Assert.False(details.RemoveThisCopyCommand.CanExecute(null));

            // Duplicate twice (e.g. OP 2 and OP 3) right inside the modal.
            details.DuplicateEntryCommand.Execute(null);
            Assert.Contains("#2", details.DuplicateFeedback, StringComparison.Ordinal);
            details.DuplicateEntryCommand.Execute(null);
            Assert.Contains("#3", details.DuplicateFeedback, StringComparison.Ordinal);
            Assert.Equal(3, details.TotalCopiesCount);

            details.OpenOnLiveChartCommand.Execute(null);
        };

        board.OpenEntryDetailsCommand.Execute(original);
        Assert.Single(harness.Dialogs.OpenedUrls);
        Assert.Equal($"https://www.livechart.me/anime/{original.Item.Value}", harness.Dialogs.OpenedUrls[0].AbsoluteUri);

        // Duplicates appear immediately next to original in Unranked with copy numbers 2 and 3.
        Assert.Equal(
            [new BoardEntryKey(original.Item, 1), new BoardEntryKey(original.Item, 2), new BoardEntryKey(original.Item, 3), new BoardEntryKey(secondShow.Item, 1)],
            board.Unranked.Entries.Take(4).Select(e => e.Key));

        var copy2 = board.Unranked.Entries[1];
        var copy3 = board.Unranked.Entries[2];
        Assert.True(copy2.IsDuplicate);
        Assert.Equal(2, copy2.CopyNumber);
        Assert.Equal(3, copy3.CopyNumber);

        // Copy #2 moves to Tier S independently while original and copy #3 remain in Unranked.
        board.Drop(copy2, board.Tiers[0], 0);
        Assert.Equal([copy2.Key], board.Tiers[0].Entries.Select(e => e.Key));
        Assert.Equal([original.Key, copy3.Key, secondShow.Key], board.Unranked.Entries.Take(3).Select(e => e.Key));

        // Open details on copy #3 and remove it via the modal's Remove button.
        var closed = false;
        harness.Dialogs.InspectEntryDetails = details =>
        {
            Assert.True(details.IsDuplicate);
            Assert.Equal(3, details.CopyNumber);
            Assert.True(details.RemoveThisCopyCommand.CanExecute(null));
            details.RequestClose += (_, _) => closed = true;
            details.RemoveThisCopyCommand.Execute(null);
        };
        board.OpenEntryDetailsCommand.Execute(copy3);
        Assert.True(closed);
        Assert.DoesNotContain(board.Unranked.Entries, e => e.Key == copy3.Key);

        // Undo restores copy #3; redo removes it again; final state survives reopen.
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => board.Unranked.Entries.Any(e => e.Key == copy3.Key), "copy #3 restored by undo");
        harness.Project.RedoCommand.Execute(null);
        await Ui.UntilAsync(() => board.Unranked.Entries.All(e => e.Key != copy3.Key), "copy #3 removed again by redo");

        await harness.ReopenAsync();
        harness.Project.SelectedBoard = harness.Project.Boards.Single();
        var reopened = Assert.IsType<BoardViewModel>(harness.Project.Content);
        Assert.Equal([new BoardEntryKey(original.Item, 2)], reopened.Tiers[0].Entries.Select(e => e.Key));
        Assert.Equal([new BoardEntryKey(original.Item, 1), new BoardEntryKey(secondShow.Item, 1)], reopened.Unranked.Entries.Take(2).Select(e => e.Key));
    });

    [Fact]
    public Task Board_entry_details_fetches_op_ed_videos_details_and_external_links_on_demand_and_caches_inside_project() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Themes", collection.Entries.Take(2).ToArray());
        var target = board.Unranked.Entries[0];
        var id = target.Item.Value;

        var videosHtml = $"""
            <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{id}/videos?page=1" /></head><body>
            <div class="lc-video" data-controller="video" data-video-id="1" data-video-duration="PT1M31S">
                <a href="https://www.youtube.com/watch?v=ed2pit" class="lc-interactive-surface__interaction-delegate"></a>
                <div data-video-target="durationBadge">1:31</div>
                <span data-video-target="auralTracklist">JA</span>
                <div class="lcm3-text--title-md">ED 2 (Pit ver.)</div>
            </div>
            <div class="lc-video" data-controller="video" data-video-id="2" data-video-duration="PT1M31S">
                <a href="https://www.youtube.com/watch?v=op2vid" class="lc-interactive-surface__interaction-delegate"></a>
                <div data-video-target="durationBadge">1:31</div>
                <div class="lcm3-text--title-md">OP 2</div>
            </div>
            <div class="lc-video" data-controller="video" data-video-id="3" data-video-duration="PT1M31S">
                <a href="https://www.youtube.com/watch?v=op1vid" class="lc-interactive-surface__interaction-delegate"></a>
                <div data-video-target="durationBadge">1:31</div>
                <div class="lcm3-text--title-md">OP</div>
            </div>
            <div class="lc-video" data-controller="video" data-video-id="4" data-video-duration="PT1M31S">
                <a href="https://www.youtube.com/watch?v=ed1vid" class="lc-interactive-surface__interaction-delegate"></a>
                <div data-video-target="durationBadge">1:31</div>
                <div class="lcm3-text--title-md">ED</div>
            </div>
            <div class="lc-video" data-controller="video" data-video-id="5" data-video-duration="PT1M58S">
                <a href="https://www.youtube.com/watch?v=mainPV_0001" class="lc-interactive-surface__interaction-delegate"></a>
                <div data-video-target="durationBadge">1:58</div>
                <span data-video-target="auralTracklist">JA</span>
                <span data-video-target="textualTracklist">EN</span>
                <div class="lcm3-text--title-md">Main PV</div>
            </div>
            </body></html>
            """;
        var profileHtml = $"""
            <!DOCTYPE html><html><body>
            <div data-controller="anime-details" data-anime-details-id="{id}">
                <div><div>Format</div><div><a href="/anime?anime_formats=4">TV</a></div></div>
                <div><div>Source</div><div><a href="/tags/10">Light Novel</a></div></div>
                <div><div>Episodes</div><div data-viewing-progression-total-count-value="12">0/12</div></div>
                <div><div>Run time</div><div>24m</div></div>
                <div><a href="/studios/1">HORNETS</a></div>
                <div><a href="/tags/1">Fantasy</a><a href="/tags/2">Isekai</a><a href="/tags/3">Romance</a></div>
                <div data-anime-details-target="synopsis"><p>Scraped on demand synopsis.</p></div>
                <a href="https://anilist.co/anime/185660">AniList</a>
                <a href="https://myanimelist.net/anime/60543">MyAnimeList</a>
            </div>
            </body></html>
            """;
        harness.Handler.Add($"https://www.livechart.me/anime/{id}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(videosHtml), "text/html");
        harness.Handler.Add($"https://www.livechart.me/anime/{id}", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(profileHtml), "text/html");

        var details = new BoardEntryDetailsViewModel(board, target, harness.Session.Current);
        Assert.False(details.HasCachedLiveData);
        Assert.Empty(details.ThemeVideos);

        var undoBefore = harness.Project.UndoLabel;
        await details.EnsureLoadedAsync();

        // Priority OP/ED tab is selected with OPs first (OP, OP 2) then EDs (ED, ED 2 (Pit ver.)), and Main PV on Other tab.
        Assert.True(details.HasCachedLiveData);
        Assert.True(details.ShowThemeVideos);
        Assert.Equal(["OP", "OP 2", "ED", "ED 2 (Pit ver.)"], details.ThemeVideos.Select(v => v.Title));
        Assert.Equal(["Main PV"], details.OtherVideos.Select(v => v.Title));
        Assert.Equal("HORNETS", details.StudiosText);
        Assert.Equal(["Fantasy", "Isekai", "Romance"], details.Tags);
        Assert.Contains("Light Novel", details.Metadata, StringComparison.Ordinal);
        Assert.Contains("12 eps × 24m", details.Metadata, StringComparison.Ordinal);
        Assert.Equal("Scraped on demand synopsis.", details.Synopsis);
        Assert.Equal(new Uri("https://anilist.co/anime/185660"), details.AniListUrl);
        Assert.Equal(new Uri("https://myanimelist.net/anime/60543"), details.MyAnimeListUrl);
        Assert.Equal(undoBefore, harness.Project.UndoLabel); // Caching extras must not pollute user undo history.

        // Clicking video, AniList, and MyAnimeList links opens verified external HTTPS URLs.
        details.OpenVideoCommand.Execute(details.ThemeVideos[0]);
        details.OpenOnAniListCommand.Execute(null);
        details.OpenOnMyAnimeListCommand.Execute(null);
        Assert.Equal(
            [
                new Uri("https://www.youtube.com/watch?v=op1vid"),
                new Uri("https://anilist.co/anime/185660"),
                new Uri("https://myanimelist.net/anime/60543"),
            ],
            harness.Dialogs.OpenedUrls);

        // Playing a row opens the in-app player for a valid YouTube ID; a link it cannot play opens externally.
        Assert.False(details.ThemeVideos[0].CanPlayInApp); // "op1vid" is not an 11-character YouTube ID.
        details.PlayVideoCommand.Execute(details.ThemeVideos[0]);
        Assert.Equal(new Uri("https://www.youtube.com/watch?v=op1vid"), harness.Dialogs.OpenedUrls[^1]);
        Assert.Empty(harness.Dialogs.ShownPlayers);
        details.PlayVideoCommand.Execute(details.OtherVideos[0]);
        var shown = Assert.Single(harness.Dialogs.ShownPlayers);
        Assert.Equal("mainPV_0001", shown.VideoId.Value);
        Assert.Equal("Video: Main PV", shown.WindowTitle);
        Assert.Equal("https://player.animetierlist.invalid/player.html?v=mainPV_0001", shown.PlayerPageUrl.AbsoluteUri);
        Assert.Equal(4, harness.Dialogs.OpenedUrls.Count);

        // Reopen project from disk: cached videos, details, and AniList/MyAnimeList links load instantly with zero extra HTTP requests.
        var requestsBeforeReopen = harness.Handler.Requests.Count;
        await harness.ReopenAsync();
        harness.Project.SelectedBoard = harness.Project.Boards.Single();
        var reopenedBoard = Assert.IsType<BoardViewModel>(harness.Project.Content);
        var reopenedDetails = new BoardEntryDetailsViewModel(reopenedBoard, reopenedBoard.Unranked.Entries[0], harness.Session.Current);
        await reopenedDetails.EnsureLoadedAsync();

        Assert.Equal(requestsBeforeReopen, harness.Handler.Requests.Count);
        Assert.True(reopenedDetails.HasCachedLiveData);
        Assert.Equal(["OP", "OP 2", "ED", "ED 2 (Pit ver.)"], reopenedDetails.ThemeVideos.Select(v => v.Title));
        Assert.Equal(["Main PV"], reopenedDetails.OtherVideos.Select(v => v.Title));
        Assert.Equal(new Uri("https://anilist.co/anime/185660"), reopenedDetails.AniListUrl);
        Assert.Equal(new Uri("https://myanimelist.net/anime/60543"), reopenedDetails.MyAnimeListUrl);
        Assert.Equal("HORNETS", reopenedDetails.StudiosText);
    });

    [Fact]
    public Task Board_entry_details_handles_partial_fetch_failure_incomplete_seasonal_metadata_and_collection_isolation() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var item = new LiveChartId(55501);

        // Collection A has both external links plus a rating, but incomplete details (no studios/synopsis);
        // Collection B shares the same anime ID with its own captured rating and synopsis.
        var snapA = new SourceSnapshot(
            SnapshotId.New(),
            new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv),
            [
                new SeasonCapture(
                    new SeasonKey(2025, Season.Summer),
                    CatalogueCategory.Tv,
                    new Uri("https://www.livechart.me/summer-2025/tv"),
                    DateTimeOffset.UnixEpoch,
                    "test",
                    [
                        new SourceOccurrence(
                            0,
                            item,
                            new SourceTitles("Shared Show", null, "Shared Show", null),
                            [],
                            new SourceDetails(
                                rating: "7.50 out of 10",
                                premiere: "Summer 2025",
                                aniListUrl: new Uri("https://anilist.co/anime/1001"),
                                myAnimeListUrl: new Uri("https://myanimelist.net/anime/2002"))),
                    ]),
            ]);
        var snapB = new SourceSnapshot(
            SnapshotId.New(),
            new AcquisitionContext(2025, Season.Fall, CatalogueCategory.Tv),
            [
                new SeasonCapture(
                    new SeasonKey(2025, Season.Fall),
                    CatalogueCategory.Tv,
                    new Uri("https://www.livechart.me/fall-2025/tv"),
                    DateTimeOffset.UnixEpoch,
                    "test",
                    [
                        new SourceOccurrence(
                            0,
                            item,
                            new SourceTitles("Shared Show", null, "Shared Show", null),
                            [],
                            new SourceDetails(rating: "9.10 out of 10", premiere: "Fall 2025", synopsis: "Collection B synopsis.")),
                    ]),
            ]);

        var colA = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Col A", snapA), true);
        var colB = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Col B", snapB), true);
        var boardAState = BoardOperations.Create(BoardId.New(), "Board A", colA, _ => new ResolvedTitle("Shared Show", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());
        var boardBState = BoardOperations.Create(BoardId.New(), "Board B", colB, _ => new ResolvedTitle("Shared Show", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());
        Assert.IsType<Application.Session.SessionResult.Accepted>(harness.Session.Execute(
            "Seed two collections",
            s => s.WithSnapshot(snapA).WithSnapshot(snapB).WithCollection(colA).WithCollection(colB).WithBoard(boardAState).WithBoard(boardBState)));

        await Ui.UntilAsync(() => harness.Project.Boards.Count == 2, "two boards registered");
        harness.Project.SelectedBoard = harness.Project.Boards.Single(b => b.Id == boardAState.Id);
        var boardA = Assert.IsType<BoardViewModel>(harness.Project.Content);

        // First open: videos fail (404), profile succeeds (provides studios + tags, no rating/synopsis so snapshot stays distinct).
        var profileHtml = $"""
            <!DOCTYPE html><html><body>
            <div data-controller="anime-details" data-anime-details-id="{item.Value}">
                <div><a href="/studios/1">Studio Partial</a></div>
                <div><a href="/tags/1">Action</a></div>
                <a href="https://anilist.co/anime/1001">AniList</a>
                <a href="https://myanimelist.net/anime/2002">MyAnimeList</a>
            </div>
            </body></html>
            """;
        var videosHtml = $"""
            <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{item.Value}/videos?page=1" /></head><body>
            <div class="lc-video" data-controller="video" data-video-id="1">
                <a href="https://www.youtube.com/watch?v=oponly" class="lc-interactive-surface__interaction-delegate"></a>
                <div class="lcm3-text--title-md">OP</div>
            </div>
            </body></html>
            """;
        harness.Handler.Add($"https://www.livechart.me/anime/{item.Value}/videos", System.Net.HttpStatusCode.NotFound);
        harness.Handler.Add($"https://www.livechart.me/anime/{item.Value}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(videosHtml), "text/html");
        harness.Handler.Add($"https://www.livechart.me/anime/{item.Value}", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(profileHtml), "text/html");

        var detailsA = new BoardEntryDetailsViewModel(boardA, boardA.Unranked.Entries[0], harness.Session.Current);
        await detailsA.EnsureLoadedAsync();

        // S1: Profile was fetched despite Col A already having both links and a rating.
        Assert.Equal("Studio Partial", detailsA.StudiosText);
        Assert.Equal(["Action"], detailsA.Tags);
        // S4: Videos fetch failed, so error is shown and "no videos listed" empty notice stays hidden.
        Assert.False(string.IsNullOrEmpty(detailsA.LiveDataError));
        Assert.False(detailsA.HasNoThemeVideos);
        Assert.False(detailsA.HasNoOtherVideos);
        // S3: Cached ItemExtras stores ONLY profile data, never Col A's snapshot rating/premiere.
        Assert.Null(harness.Session.Current.ItemExtras[item].Details.Rating);
        Assert.Null(harness.Session.Current.ItemExtras[item].Details.Premiere);

        // S2: Retry fetches ONLY /videos (does not repeat /anime/{id}) and succeeds.
        var profileCountBeforeRetry = harness.Handler.CountFor($"https://www.livechart.me/anime/{item.Value}");
        await detailsA.RetryLiveDataCommand.ExecuteAsync(null);

        Assert.Equal(profileCountBeforeRetry, harness.Handler.CountFor($"https://www.livechart.me/anime/{item.Value}"));
        Assert.Equal(["OP"], detailsA.ThemeVideos.Select(v => v.Title));
        Assert.Empty(detailsA.LiveDataError);
        Assert.True(detailsA.HasCachedLiveData);

        // S3: Opening the same anime from Collection B keeps Collection B's own rating, premiere, and synopsis!
        harness.Project.SelectedBoard = harness.Project.Boards.Single(b => b.Id == boardBState.Id);
        var boardB = Assert.IsType<BoardViewModel>(harness.Project.Content);
        var detailsB = new BoardEntryDetailsViewModel(boardB, boardB.Unranked.Entries[0], harness.Session.Current);
        Assert.Equal("9.10 out of 10", detailsB.Rating);
        Assert.Equal("Fall 2025", detailsB.Premiere);
        Assert.Equal("Collection B synopsis.", detailsB.Synopsis);
        Assert.Equal("Studio Partial", detailsB.StudiosText);
        Assert.Equal(["OP"], detailsB.ThemeVideos.Select(v => v.Title));

        // S2 + S4 on Refresh: after both pages are cached (with 0 videos), if Refresh fails on /videos alone,
        // empty notice stays hidden alongside error, and Retry repeats ONLY /videos (not /anime/{id}).
        var emptyVideosHtml = $"""
            <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{item.Value}/videos?page=1" /></head><body>
            <h1>Videos</h1>
            <a href="/anime/{item.Value}/videos">All</a>
            <p>There are no videos for this anime yet.</p>
            </body></html>
            """;
        harness.Handler.Add($"https://www.livechart.me/anime/{item.Value}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(emptyVideosHtml), "text/html");
        harness.Handler.Add($"https://www.livechart.me/anime/{item.Value}/videos", System.Net.HttpStatusCode.NotFound);
        harness.Handler.Add($"https://www.livechart.me/anime/{item.Value}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(videosHtml), "text/html");

        // Consume prior queued video response so Refresh 1 sees empty videos, Refresh 2 sees 404, Retry sees videosHtml.
        await detailsB.RefreshLiveDataCommand.ExecuteAsync(null);
        await detailsB.RefreshLiveDataCommand.ExecuteAsync(null);
        Assert.True(detailsB.HasNoThemeVideos); // Empty list succeeded with 0 videos.

        await detailsB.RefreshLiveDataCommand.ExecuteAsync(null); // Now /videos returns 404, /anime/{id} succeeds.
        Assert.Contains("Videos:", detailsB.LiveDataError, StringComparison.Ordinal);
        Assert.False(detailsB.HasNoThemeVideos); // S4: Do not claim "no videos listed" while video refresh errored.

        var profileBeforeTargetedRetry = harness.Handler.CountFor($"https://www.livechart.me/anime/{item.Value}");
        await detailsB.RetryLiveDataCommand.ExecuteAsync(null); // S2: Must retry ONLY /videos, not /anime/{id}!
        Assert.Equal(profileBeforeTargetedRetry, harness.Handler.CountFor($"https://www.livechart.me/anime/{item.Value}"));
        Assert.Empty(detailsB.LiveDataError);
        Assert.Equal(["OP"], detailsB.ThemeVideos.Select(v => v.Title));
        detailsA.Dispose();
        detailsB.Dispose();
    });

    [Fact]
    public Task Board_entry_details_recovers_suspended_saves_from_modal_and_retains_staged_data_across_close() => Ui.RunAsync(async () =>
    {
        var failCommits = false;
        await using var harness = await DesktopHarness.CreateAsync(
            serveCovers: false,
            wrapStore: inner => new ToggleFailStore(inner, () => failCommits));
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Suspended", collection.Entries.Take(2).ToArray());
        await harness.Session.FlushAsync();

        var entry1 = board.Unranked.Entries[0];
        var entry2 = board.Unranked.Entries[1];
        foreach (var entry in new[] { entry1, entry2 })
        {
            var id = entry.Item.Value;
            var videosHtml = $"""
                <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{id}/videos?page=1" /></head><body>
                <div class="lc-video" data-controller="video" data-video-id="{id}">
                    <a href="https://www.youtube.com/watch?v=saved{id}" class="lc-interactive-surface__interaction-delegate"></a>
                    <div class="lcm3-text--title-md">OP</div>
                </div>
                </body></html>
                """;
            var profileHtml = $"""
                <!DOCTYPE html><html><body>
                <div data-controller="anime-details" data-anime-details-id="{id}">
                    <a href="https://anilist.co/anime/{id}">AniList</a>
                </div>
                </body></html>
                """;
            harness.Handler.Add($"https://www.livechart.me/anime/{id}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(videosHtml), "text/html");
            harness.Handler.Add($"https://www.livechart.me/anime/{id}", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(profileHtml), "text/html");
        }

        // Part 1 (S5 modal-in-place recovery): trigger a save failure, fetch inside the modal, then recover directly from the modal's RetrySaveCommand.
        failCommits = true;
        board.Drop(entry1, board.Tiers[0], 0);
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "save failure surfaced");

        using (var details1 = new BoardEntryDetailsViewModel(board, entry1, harness.Session.Current))
        {
            await details1.EnsureLoadedAsync();
            Assert.Equal(["OP"], details1.ThemeVideos.Select(v => v.Title));
            Assert.Empty(details1.LiveDataError);
            Assert.Contains("Retry save", details1.SaveError, StringComparison.Ordinal);
            Assert.True(details1.RetrySaveCommand.CanExecute(null));
            Assert.False(details1.HasCachedLiveData);
            Assert.False(harness.Session.Current.ItemExtras.ContainsKey(entry1.Item));

            // Recover directly from inside the modal dialog with zero extra HTTP requests.
            failCommits = false;
            var requestsBeforeModalRetry = harness.Handler.Requests.Count;
            await details1.RetrySaveCommand.ExecuteAsync(null);
            Assert.Equal(requestsBeforeModalRetry, harness.Handler.Requests.Count);
            Assert.Empty(details1.SaveError);
            Assert.True(details1.HasCachedLiveData);
            Assert.False(harness.Project.SaveFailed);
            Assert.True(harness.Session.Durable.ItemExtras.ContainsKey(entry1.Item));
        }

        // Part 2 (S5 close-dialog retention): trigger another save failure, fetch entry2, close the dialog, then recover from the main window.
        failCommits = true;
        board.Drop(entry2, board.Tiers[0], 1);
        await Ui.UntilAsync(() => harness.Project.SaveFailed, "second save failure surfaced");

        using (var details2 = new BoardEntryDetailsViewModel(board, entry2, harness.Session.Current))
        {
            await details2.EnsureLoadedAsync();
            Assert.Equal(["OP"], details2.ThemeVideos.Select(v => v.Title));
            Assert.Contains("Retry save", details2.SaveError, StringComparison.Ordinal);
        }

        // Dialog is closed; main window RetrySaveCommand must recover both the board drop and entry2's staged extras.
        failCommits = false;
        var requestsBeforeMainRetry = harness.Handler.Requests.Count;
        await harness.Project.RetrySaveCommand.ExecuteAsync(null);
        await Ui.UntilAsync(() => !harness.Project.SaveFailed, "main window retry recovered");
        Assert.Equal(requestsBeforeMainRetry, harness.Handler.Requests.Count);
        Assert.True(harness.Session.Durable.ItemExtras.ContainsKey(entry2.Item));

        await harness.ReopenAsync();
        Assert.Equal(["OP"], harness.Session.Current.ItemExtras[entry1.Item].Videos.Select(v => v.Title));
        Assert.Equal(["OP"], harness.Session.Current.ItemExtras[entry2.Item].Videos.Select(v => v.Title));
    });

    [Fact]
    public Task Board_entry_details_surfaces_async_commit_failure_and_supports_save_only_retry() => Ui.RunAsync(async () =>
    {
        var failCommits = false;
        await using var harness = await DesktopHarness.CreateAsync(
            serveCovers: false,
            wrapStore: inner => new ToggleFailStore(inner, () => failCommits));
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "AsyncCommitFail", collection.Entries.Take(1).ToArray());
        await harness.Session.FlushAsync();

        var entry = board.Unranked.Entries[0];
        var id = entry.Item.Value;
        var videosHtml = $"""
            <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{id}/videos?page=1" /></head><body>
            <div class="lc-video" data-controller="video" data-video-id="1">
                <a href="https://www.youtube.com/watch?v=asyncfail" class="lc-interactive-surface__interaction-delegate"></a>
                <div class="lcm3-text--title-md">OP</div>
            </div>
            </body></html>
            """;
        var profileHtml = $"""
            <!DOCTYPE html><html><body>
            <div data-controller="anime-details" data-anime-details-id="{id}">
                <a href="https://anilist.co/anime/555">AniList</a>
            </div>
            </body></html>
            """;
        harness.Handler.Add($"https://www.livechart.me/anime/{id}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(videosHtml), "text/html");
        harness.Handler.Add($"https://www.livechart.me/anime/{id}", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(profileHtml), "text/html");

        // Session is healthy when RecordItemExtras queues the write, then SQLite rejects the async commit.
        failCommits = true;
        using var details = new BoardEntryDetailsViewModel(board, entry, harness.Session.Current);
        await details.EnsureLoadedAsync();

        Assert.Equal(["OP"], details.ThemeVideos.Select(v => v.Title));
        Assert.Empty(details.LiveDataError);
        Assert.Contains("Simulated disk full", details.SaveError, StringComparison.Ordinal);
        Assert.False(details.HasCachedLiveData);
        Assert.True(details.RetrySaveCommand.CanExecute(null));

        // Save-only retry inside the dialog commits the queued write without repeating LiveChart HTTP requests.
        failCommits = false;
        var requestsBeforeSaveRetry = harness.Handler.Requests.Count;
        await details.RetrySaveCommand.ExecuteAsync(null);
        Assert.Equal(requestsBeforeSaveRetry, harness.Handler.Requests.Count);
        Assert.Empty(details.SaveError);
        Assert.True(details.HasCachedLiveData);

        await harness.ReopenAsync();
        Assert.Equal(["OP"], harness.Session.Current.ItemExtras[entry.Item].Videos.Select(v => v.Title));
    });

    [Fact]
    public Task Board_entry_details_shows_and_saves_videos_before_the_profile_page_returns() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "Progressive", collection.Entries.Take(2).ToArray());
        await harness.Session.FlushAsync();

        var entry1 = board.Unranked.Entries[0];
        var entry2 = board.Unranked.Entries[1];
        foreach (var entry in new[] { entry1, entry2 })
        {
            var id = entry.Item.Value;
            var videosHtml = $"""
                <!DOCTYPE html><html><head><link rel="canonical" href="https://www.livechart.me/anime/{id}/videos?page=1" /></head><body>
                <div class="lc-video" data-controller="video" data-video-id="1">
                    <a href="https://www.youtube.com/watch?v=early{id}" class="lc-interactive-surface__interaction-delegate"></a>
                    <div class="lcm3-text--title-md">OP</div>
                </div>
                </body></html>
                """;
            var profileHtml = $"""
                <!DOCTYPE html><html><body>
                <div data-controller="anime-details" data-anime-details-id="{id}">
                    <a href="https://anilist.co/anime/{id}">AniList</a>
                </div>
                </body></html>
                """;
            harness.Handler.Add($"https://www.livechart.me/anime/{id}/videos", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(videosHtml), "text/html");
            harness.Handler.Add($"https://www.livechart.me/anime/{id}", System.Net.HttpStatusCode.OK, System.Text.Encoding.UTF8.GetBytes(profileHtml), "text/html");
        }

        // Hold every profile request open until the test releases it.
        TaskCompletionSource profileStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProfile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Handler.BeforeResponse = async (request, token) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/videos", StringComparison.Ordinal))
            {
                profileStarted.TrySetResult();
                await releaseProfile.Task.WaitAsync(token);
            }
        };

        // Part 1: the video list is shown and durably saved while the profile request is still in flight.
        var videosUrl1 = $"https://www.livechart.me/anime/{entry1.Item.Value}/videos";
        var profileUrl1 = $"https://www.livechart.me/anime/{entry1.Item.Value}";
        var requestsBefore = harness.Handler.RequestCount;
        using (var details1 = new BoardEntryDetailsViewModel(board, entry1, harness.Session.Current))
        {
            var load = details1.EnsureLoadedAsync();
            await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(["OP"], details1.ThemeVideos.Select(v => v.Title));
            Assert.True(details1.IsLoadingLiveData);
            Assert.False(details1.IsLoadingVideos);
            Assert.True(details1.HasNoOtherVideos); // The empty notice for loaded videos does not wait for the profile page.
            Assert.Contains("anime details", details1.LiveDataStatus, StringComparison.Ordinal);
            Assert.Null(details1.AniListUrl);
            var savedVideos = harness.Session.Durable.ItemExtras[entry1.Item];
            Assert.True(savedVideos.VideosFetched);
            Assert.False(savedVideos.ProfileFetched);

            releaseProfile.TrySetResult();
            await load;

            // Same requests in the same order as before: videos page first, then the profile page.
            Assert.Equal([videosUrl1, profileUrl1], harness.Handler.Requests.Skip(requestsBefore).Select(r => r.RequestUri!.AbsoluteUri));

            Assert.Equal(new Uri($"https://anilist.co/anime/{entry1.Item.Value}"), details1.AniListUrl);
            Assert.False(details1.IsLoadingLiveData);
            Assert.True(details1.HasCachedLiveData);
            Assert.Empty(details1.LiveDataError);
        }

        // Part 2: closing the dialog while the profile page loads keeps the already-fetched videos.
        profileStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseProfile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var videosUrl2 = $"https://www.livechart.me/anime/{entry2.Item.Value}/videos";
        using var details2 = new BoardEntryDetailsViewModel(board, entry2, harness.Session.Current);
        var load2 = details2.EnsureLoadedAsync();
        await profileStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        details2.Dispose();
        await load2.WaitAsync(TimeSpan.FromSeconds(5)); // Closing must cancel the held profile request, not wait out HTTP timeouts.

        var kept = harness.Session.Durable.ItemExtras[entry2.Item];
        Assert.Equal(["OP"], kept.Videos.Select(v => v.Title));
        Assert.True(kept.VideosFetched);
        Assert.False(kept.ProfileFetched);

        // Reopening shows the saved videos at once and fetches only the missing profile page.
        releaseProfile.TrySetResult();
        var videoRequestsBefore = harness.Handler.CountFor(videosUrl2);
        using var reopened = new BoardEntryDetailsViewModel(board, entry2, harness.Session.Current);
        Assert.Equal(["OP"], reopened.ThemeVideos.Select(v => v.Title));
        await reopened.EnsureLoadedAsync();
        Assert.Equal(videoRequestsBefore, harness.Handler.CountFor(videosUrl2));
        Assert.Equal(new Uri($"https://anilist.co/anime/{entry2.Item.Value}"), reopened.AniListUrl);
        Assert.True(reopened.HasCachedLiveData);
    });

    private sealed class ToggleFailStore(Application.Storage.IProjectStore inner, Func<bool> shouldFail) : Application.Storage.IProjectStore
    {
        public Task<Application.Storage.ProjectData> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);

        public Task CommitAsync(Application.Storage.ProjectChange change, CancellationToken cancellationToken) =>
            shouldFail() ? throw new Application.Storage.ProjectStoreException("Simulated disk full") : inner.CommitAsync(change, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task<BoardViewModel> CreateBoardAsync(DesktopHarness harness, CollectionViewModel collection, string name, EntryViewModel[] include)
    {
        foreach (var entry in include)
        {
            entry.Included = true;
        }

        collection.NewBoardName = name;
        await collection.CreateBoardCommand.ExecuteAsync(null);
        return Assert.IsType<BoardViewModel>(harness.Project.Content);
    }

    private static void Select(BoardViewModel board, BoardEntryViewModel entry) =>
        board.Tiers.Append(board.Unranked).Single(t => t.Entries.Contains(entry)).SelectedEntry = entry;
}
