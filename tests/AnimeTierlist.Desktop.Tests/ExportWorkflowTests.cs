using System.IO;
using AnimeTierlist.Desktop.ViewModels;
using SkiaSharp;

namespace AnimeTierlist.Desktop.Tests;

/// <summary>Export workflows through the view models: saved-revision PNG, cover folders, and project archives.</summary>
public sealed class ExportWorkflowTests
{
    [Fact]
    public Task Board_png_is_exported_from_the_saved_revision_and_states_its_scope() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers selected");
        var board = await CreateBoardAsync(harness, collection, "Exported", 3);
        board.Tiers[0].SelectedEntry = null;
        board.Unranked.SelectedEntry = board.Unranked.Entries[0];
        board.MoveToTierCommand.Execute("1");
        var output = Path.Combine(harness.ExportFolder, "board.png");
        harness.Dialogs.SaveFile = output;

        await board.ExportPngCommand.ExecuteAsync(null);

        Assert.Equal("Export 'Exported' as PNG (tiers and unranked shows)", harness.Dialogs.FileDialogTitles.Last());
        using var bitmap = SKBitmap.Decode(output);
        Assert.NotNull(bitmap);
        Assert.Equal(1600, bitmap.Width);
        Assert.Contains("Exported 'Exported'", harness.Project.Status, StringComparison.Ordinal);

        board.IncludeUnrankedInPng = false;
        await board.ExportPngCommand.ExecuteAsync(null);
        Assert.Equal("Export 'Exported' as PNG (tiers only, unranked shows omitted)", harness.Dialogs.FileDialogTitles.Last());
        using var tiersOnly = SKBitmap.Decode(output);
        Assert.True(tiersOnly.Height < bitmap.Height);
    });

    [Fact]
    public Task Board_png_with_a_missing_cover_explains_and_writes_nothing() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync(serveCovers: false);
        var collection = await harness.CreateCollectionAsync();
        var board = await CreateBoardAsync(harness, collection, "No covers", 2);
        var output = Path.Combine(harness.ExportFolder, "none.png");
        harness.Dialogs.SaveFile = output;

        await board.ExportPngCommand.ExecuteAsync(null);

        var (title, message) = Assert.Single(harness.Dialogs.Messages);
        Assert.Equal("PNG not exported", title);
        Assert.Contains($"[{collection.Entries[0].Item.Value}] (no cover)", message, StringComparison.Ordinal);
        Assert.Contains($"[{collection.Entries[1].Item.Value}] (no cover)", message, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    });

    [Fact]
    public Task A_file_created_after_choosing_a_new_name_is_never_replaced() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers selected");
        var board = await CreateBoardAsync(harness, collection, "Race", 1);
        var output = Path.Combine(harness.ExportFolder, "race.png");
        harness.Dialogs.SaveFile = output;
        harness.Dialogs.AfterSaveChoice = () => File.WriteAllText(output, "someone else's file");

        await board.ExportPngCommand.ExecuteAsync(null);

        Assert.Equal("someone else's file", File.ReadAllText(output));
        Assert.Equal("PNG not exported", Assert.Single(harness.Dialogs.Messages).Title);
    });

    [Fact]
    public Task Cover_export_uses_included_shows_in_order_regardless_of_search() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers selected");
        foreach (var entry in collection.Entries.Take(4))
        {
            entry.Included = true;
        }

        collection.SearchText = collection.Entries[2].Title;
        harness.Dialogs.Folder = harness.ExportFolder;

        await collection.ExportCoversCommand.ExecuteAsync(null);

        var folder = Path.Combine(harness.ExportFolder, "Summer 2025 · Movies covers");
        var files = Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(5, files.Count);
        Assert.Equal(collection.Entries.Take(4).Select((e, i) => $"{i + 1:D4} "), files.Take(4).Select(f => f![..5]));
        Assert.All(collection.Entries.Take(4).Zip(files.Take(4)), p => Assert.EndsWith($"[{p.First.Item.Value}].jpg", p.Second, StringComparison.Ordinal));

        await collection.ExportCoversCommand.ExecuteAsync(null);
        Assert.True(Directory.Exists(folder + " (2)"));
    });

    [Fact]
    public Task Project_archive_exports_and_imports_as_a_new_project() => Ui.RunAsync(async () =>
    {
        await using var harness = await DesktopHarness.CreateAsync();
        var collection = await harness.CreateCollectionAsync();
        await Ui.UntilAsync(() => collection.Entries.All(e => e.HasCover), "covers selected");
        await CreateBoardAsync(harness, collection, "Archived", 2);
        var archive = Path.Combine(harness.ExportFolder, "Test.animerank");
        harness.Dialogs.SaveFile = archive;

        await harness.Project.ExportProjectCommand.ExecuteAsync(null);
        Assert.True(File.Exists(archive), harness.Project.Status);

        var shell = new ShellViewModel(harness.App);
        harness.Dialogs.OpenFile = archive;
        await shell.ImportProjectCommand.ExecuteAsync(null);
        try
        {
            Assert.Contains("Test (2)", shell.ProjectNames);
            var imported = Assert.IsType<ProjectViewModel>(shell.Project);
            Assert.Equal("Test (2)", imported.Name);
            Assert.Equal(["Archived"], imported.Boards.Select(b => b.Label));
            Assert.Equal(harness.Session.Current.Collections.Values.Single().Entries.Select(e => e.Item), imported.Session.Current.Collections.Values.Single().Entries.Select(e => e.Item));
        }
        finally
        {
            await shell.CloseProjectAsync();
        }
    });

    private static async Task<BoardViewModel> CreateBoardAsync(DesktopHarness harness, CollectionViewModel collection, string name, int included)
    {
        foreach (var entry in collection.Entries.Take(included))
        {
            entry.Included = true;
        }

        collection.NewBoardName = name;
        await collection.CreateBoardCommand.ExecuteAsync(null);
        return Assert.IsType<BoardViewModel>(harness.Project.Content);
    }
}
