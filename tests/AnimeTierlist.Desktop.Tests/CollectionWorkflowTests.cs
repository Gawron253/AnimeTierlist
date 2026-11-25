using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using AnimeTierlist.Application.Covers;
using AnimeTierlist.Core;
using AnimeTierlist.Desktop.ViewModels;
using AnimeTierlist.Infrastructure.Assets;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>
/// Workflows through the real view models, session, SQLite store, asset store, and LiveChart
/// adapters, with HTTP served from frozen fixtures. Commands are invoked as the keyboard-bound buttons invoke them.
/// </summary>
public sealed class CollectionWorkflowTests
{
    [Fact]
    public Task New_collection_keeps_the_source_order_starts_included_and_gets_covers() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();

        var view = await harness.CreateCollectionAsync(excludeAll: false);

        Assert.Equal(DesktopHarness.OracleIds(), view.Entries.Select(e => e.Item.Value));
        Assert.Equal(Enumerable.Range(1, view.Entries.Count), view.Entries.Select(e => e.Position));
        Assert.All(view.Entries, e => Assert.True(e.Included));
        await Ui.UntilAsync(() => view.Entries.All(e => e.HasCover), "all covers selected");
        Assert.All(harness.Collection(view).Entries, e => Assert.NotNull(e.Cover));
        Assert.Equal(1, harness.Handler.CountFor(DesktopHarness.PageUrl));
        Assert.Single(harness.Project.Collections);
    });

    [Fact]
    public Task A_closed_project_reports_cancellation_instead_of_throwing() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var closed = harness.Project;

        await harness.ReopenAsync();

        Assert.True(closed.Lifetime.IsCancellationRequested);
    });

    [Fact]
    public Task Edit_shows_at_once_with_Saving_until_its_commit_finishes() => Ui.RunAsync(async () =>
    {
        HeldCommitStore? held = null;
        await using var harness = await DesktopHarness.CreateAsync(wrapStore: s => held = new HeldCommitStore(s));
        var view = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => harness.Project.SaveText == "Saved", "the collection saved");

        var release = held!.Hold();
        try
        {
            view.SelectedEntry = view.Entries[0];
            view.ToggleSelectedCommand.Execute(null);

            // The edit is visible while its commit is still pending, and the indicator says so.
            await Ui.UntilAsync(() => view.Entries[0].Included && harness.Project.SaveText == "Saving...", "the included entry with the saving indicator");
        }
        finally
        {
            release();
        }

        await Ui.UntilAsync(() => harness.Project.SaveText == "Saved", "Saved after the commit");
        Assert.True(harness.Collection(view).Entries[0].Included);
    });

    [Fact]
    public Task Unreachable_source_creates_nothing_and_explains_that_proxy_networks_are_unsupported() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(servePage: false);
        harness.Handler.Add(DesktopHarness.PageUrl, () => throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made."));
        var acquisition = harness.Project.Acquisition;
        acquisition.YearText = "2025";
        acquisition.Season = acquisition.SeasonOptions.Single(o => o.Label == "Summer");
        acquisition.Category = acquisition.CategoryOptions.Single(o => o.Label == "Movies");

        await acquisition.FetchCommand.ExecuteAsync(null);

        Assert.False(acquisition.HasPreview);
        Assert.Contains("could not be reached", acquisition.Failure, StringComparison.Ordinal);
        Assert.Contains("does not use proxy servers", acquisition.Failure, StringComparison.Ordinal);
        Assert.Empty(harness.Project.Collections);
        Assert.Empty(harness.Session.Current.Collections);
        Assert.Equal(3, harness.Handler.CountFor(DesktopHarness.PageUrl));
    });

    [Fact]
    public Task Changing_the_selection_after_a_preview_disables_create_and_bad_years_are_rejected() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var acquisition = harness.Project.Acquisition;
        acquisition.YearText = "2025";
        acquisition.Season = acquisition.SeasonOptions.Single(o => o.Label == "Summer");
        acquisition.Category = acquisition.CategoryOptions.Single(o => o.Label == "Movies");
        await acquisition.FetchCommand.ExecuteAsync(null);
        Assert.True(acquisition.CreateCommand.CanExecute(null));

        acquisition.Season = acquisition.SeasonOptions.Single(o => o.Label == "Fall");

        Assert.False(acquisition.HasPreview);
        Assert.False(acquisition.CreateCommand.CanExecute(null));
        Assert.Empty(acquisition.PreviewEntries);

        acquisition.YearText = "20x5";
        await acquisition.FetchCommand.ExecuteAsync(null);

        Assert.Contains("Enter the year as a number", acquisition.Failure, StringComparison.Ordinal);
        Assert.Equal(1, harness.Handler.CountFor(DesktopHarness.PageUrl));
    });

    [Fact]
    public Task Typing_a_custom_title_survives_unrelated_project_updates() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var view = await harness.CreateCollectionAsync();
        view.SelectedEntry = view.Entries[1];

        view.TitleOverrideText = "My title in progress";
        view.Entries[0].Included = true;
        await Ui.UntilAsync(() => harness.Project.Collections[0].Label.Contains("(1/", StringComparison.Ordinal), "project refreshed after the edit");

        Assert.Equal("My title in progress", view.TitleOverrideText);
        view.SetTitleOverrideCommand.Execute(null);
        Assert.Equal("My title in progress", harness.Collection(view).Entry(view.Entries[1].Item).TitleOverride);
    });

    [Fact]
    public Task Include_checkbox_is_an_undoable_command_and_search_never_changes_membership_or_order() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var view = await harness.CreateCollectionAsync();
        var order = view.Entries.Select(e => e.Item).ToList();

        view.Entries[2].Included = true;

        Assert.True(harness.Collection(view).Entry(order[2]).Included);
        await Ui.UntilAsync(() => harness.Project.UndoLabel == "Include entry", "undo label Include entry");
        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => !view.Entries[2].Included, "undo reflected in the checkbox");
        Assert.False(harness.Collection(view).Entry(order[2]).Included);

        var target = view.Entries[5];
        view.SearchText = target.Title;
        var shown = view.View.Cast<EntryViewModel>().ToList();
        Assert.Contains(target, shown);
        Assert.True(shown.Count < view.Entries.Count);
        view.IncludeVisibleCommand.Execute(null);
        view.SearchText = string.Empty;

        var collection = harness.Collection(view);
        Assert.Equal(order, collection.Entries.Select(e => e.Item));
        Assert.Equal(shown.Select(e => e.Item).Order(), collection.IncludedEntries.Select(e => e.Item).Order());
        Assert.Equal(view.Entries.Count, view.View.Cast<object>().Count());
    });

    [Fact]
    public Task Move_commands_reorder_durably_and_keep_the_same_entry_objects() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var view = await harness.CreateCollectionAsync();
        var moved = view.Entries[3];

        view.SelectedEntry = moved;
        view.MoveToTopCommand.Execute(null);
        await Ui.UntilAsync(() => view.Entries[0] == moved, "moved entry at the top");
        view.MoveDownCommand.Execute(null);
        await Ui.UntilAsync(() => view.Entries[1] == moved, "moved entry second");

        Assert.Equal(2, moved.Position);
        Assert.Equal(moved.Item, harness.Collection(view).Entries[1].Item);
        await harness.Session.FlushAsync();
        Assert.Equal(moved.Item, harness.Session.Durable.Collection(view.Id).Entries[1].Item);
    });

    [Fact]
    public Task Delete_asks_first_is_blocked_by_boards_and_can_be_undone() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var view = await harness.CreateCollectionAsync();

        // Bound as in the main window: removing the selected item makes the list box clear the selection first.
        var list = new ListBox { DataContext = harness.Project };
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(ProjectViewModel.Collections)));
        list.SetBinding(Selector.SelectedItemProperty, new Binding(nameof(ProjectViewModel.SelectedCollection)) { Mode = BindingMode.TwoWay });
        Assert.Same(harness.Project.SelectedCollection, list.SelectedItem);

        harness.Dialogs.Answer = false;
        view.DeleteCommand.Execute(null);
        Assert.Single(harness.Session.Current.Collections);

        harness.Dialogs.Answer = true;
        view.DeleteCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.Collections.Count == 0, "collection removed from the list");
        Assert.Empty(harness.Session.Current.Collections);
        Assert.IsType<AcquisitionViewModel>(harness.Project.Content);
        Assert.Equal(2, harness.Dialogs.Questions.Count);

        harness.Project.UndoCommand.Execute(null);
        await Ui.UntilAsync(() => harness.Project.Collections.Count == 1, "collection restored by undo");

        var board = BoardId.New();
        harness.Project.Execute("Include all", s => s.WithCollection(CollectionOperations.SetAllIncluded(s.Collection(view.Id), true)));
        harness.Project.Execute("Create board", s =>
        {
            var collection = s.Collection(view.Id);
            return s.WithBoard(BoardOperations.Create(board, "Favourites", collection, e => s.ResolveTitle(collection, e), _ => true, BoardOperations.DefaultTiers()));
        });
        harness.Project.SelectedCollection = harness.Project.Collections[0];
        var restored = Assert.IsType<CollectionViewModel>(harness.Project.Content);

        restored.DeleteCommand.Execute(null);

        Assert.Single(harness.Session.Current.Collections);
        Assert.Contains("Favourites", harness.Project.Status, StringComparison.Ordinal);
        Assert.Equal(2, harness.Dialogs.Questions.Count);
    });

    [Fact]
    public Task Broken_cover_can_be_marked_missing_offline_and_undo_restores_the_reference() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var view = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => view.Entries.All(e => e.HasCover), "all covers selected");
        var entry = view.Entries[0];
        var broken = harness.Collection(view).Entry(entry.Item).Cover!;
        var healthy = view.Entries[1];

        view.SelectedEntry = healthy;
        await Ui.UntilAsync(() => view.SelectedCoverText.Contains("intact", StringComparison.Ordinal), "healthy cover inspected");
        Assert.False(view.MarkCoverMissingCommand.CanExecute(null));

        Damage(harness, broken.Asset);
        view.SelectedEntry = entry;
        await Ui.UntilAsync(() => view.SelectedCoverBroken, "broken cover detected");
        var requests = harness.Handler.Requests.Count;
        harness.Dialogs.Answer = true;

        await view.MarkCoverMissingCommand.ExecuteAsync(null);

        Assert.Null(harness.Collection(view).Entry(entry.Item).Cover);
        Assert.Equal(requests, harness.Handler.Requests.Count);
        await Ui.UntilAsync(() => harness.Project.UndoLabel == "Mark cover missing", "undo label Mark cover missing");
        await Ui.UntilAsync(() => !view.SelectedCoverBroken, "actions disabled after recovery");

        harness.Project.UndoCommand.Execute(null);

        Assert.Equal(broken, harness.Collection(view).Entry(entry.Item).Cover);
        await Ui.UntilAsync(() => view.SelectedCoverBroken, "broken reference restored by undo");
    });

    [Fact]
    public Task Cover_damaged_while_closed_is_shown_as_damaged_on_open_without_a_request() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var created = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => created.Entries.All(e => e.HasCover), "all covers selected");
        var damaged = created.Entries[2].Item;
        Damage(harness, harness.Collection(created).Entry(damaged).Cover!.Asset);

        await harness.ReopenAsync();
        var requests = harness.Handler.Requests.Count;
        var view = Assert.IsType<CollectionViewModel>(harness.Project.Content);
        var entry = view.Entries.Single(e => e.Item == damaged);

        await Ui.UntilAsync(() => entry.CoverPlaceholder == "Damaged cover", "damaged cover marked in the list");
        Assert.False(entry.HasCover);
        Assert.Null(entry.Thumbnail);
        Assert.Contains("missing or damaged", entry.CoverNote, StringComparison.Ordinal);
        Assert.Equal(view.Entries.Count - 1, view.Entries.Count(e => e.HasCover));
        Assert.StartsWith("1 entry has a missing or damaged cover file.", view.CoverStatus, StringComparison.Ordinal);
        Assert.NotNull(harness.Collection(view).Entry(damaged).Cover);

        view.SelectedEntry = entry;
        await Ui.UntilAsync(() => view.SelectedCoverBroken, "broken cover selected");
        harness.Dialogs.Answer = true;
        await view.MarkCoverMissingCommand.ExecuteAsync(null);

        await Ui.UntilAsync(() => view.CoverStatus.Length == 0, "damage summary cleared");
        Assert.Equal("No cover", entry.CoverPlaceholder);
        Assert.Equal(requests, harness.Handler.Requests.Count);
    });

    [Fact]
    public Task Different_cover_is_shown_for_review_and_used_only_when_accepted() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var view = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => view.Entries.All(e => e.HasCover), "all covers selected");
        var entry = view.Entries[0];
        var broken = harness.Collection(view).Entry(entry.Item).Cover!;
        Damage(harness, broken.Asset);
        harness.CoverBytes[broken.RequestedUrl.AbsoluteUri] = DesktopHarness.Jpeg(250);

        await view.FetchCoversCommand.ExecuteAsync(null);
        view.SelectedEntry = entry;
        await Ui.UntilAsync(() => view.OfferThumbnail is not null, "new cover offered");

        Assert.Equal(broken, harness.Collection(view).Entry(entry.Item).Cover);
        Assert.True(view.UseNewCoverCommand.CanExecute(null));

        await view.UseNewCoverCommand.ExecuteAsync(null);

        var replaced = harness.Collection(view).Entry(entry.Item).Cover!;
        Assert.NotEqual(broken.Asset, replaced.Asset);
        Assert.True(harness.Session.Current.Assets.ContainsKey(replaced.Asset));
        await Ui.UntilAsync(() => harness.Project.UndoLabel == "Use new cover", "undo label Use new cover");
        await Ui.UntilAsync(() => view.SelectedCoverText.Contains("intact", StringComparison.Ordinal), "replacement inspected as intact");
        Assert.Null(view.OfferThumbnail);
    });

    private static void Damage(DesktopHarness harness, AssetHash hash)
    {
        var path = new AssetStore(harness.ProjectDirectory).PathOf(harness.Session.Current.Assets[hash]);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
    }
}
