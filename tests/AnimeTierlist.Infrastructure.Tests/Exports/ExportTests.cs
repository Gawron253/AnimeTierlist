using System.Collections.Immutable;
using System.Text.Json;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Exports;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Tests.Exports;

/// <summary>PNG and selected-cover exports from saved state, with real images, scope rules, and atomic output.</summary>
public sealed class ExportTests : IDisposable
{
    private static readonly AcquisitionContext Context = new(2025, Season.Summer, CatalogueCategory.Tv);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetStore _store;
    private readonly Dictionary<AssetHash, AssetRecord> _assets = [];
    private readonly Dictionary<LiveChartId, SKColor> _colors = [];

    public ExportTests()
    {
        _store = new AssetStore(Path.Combine(_root, "project"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Out(string name) => Path.Combine(_root, "out", name);

    [Fact]
    public async Task Png_places_each_cover_letterboxed_in_its_tier_cell_and_includes_the_pool()
    {
        var board = await BoardAsync(5, ranked: [(0, [1, 2]), (2, [3])]);

        await BoardPngExporter.ExportAsync(board, _assets, _store, Out("board.png"), includeUnranked: true, replaceExisting: false, Ct);

        var layout = BoardPngExporter.Layout(board, includeUnranked: true);
        using var bitmap = SKBitmap.Decode(Out("board.png"));
        Assert.Equal((layout.Width, layout.Height), (bitmap.Width, bitmap.Height));
        Assert.Equal(["S", "A", "B", "C", "D", "Unranked"], layout.Rows.Select(r => r.Label));
        Assert.Equal([1, 2], layout.Rows[0].Cells.Select(c => c.Item.Value));
        Assert.Equal([3], layout.Rows[2].Cells.Select(c => c.Item.Value));
        Assert.Equal([4, 5], layout.Rows[5].Cells.Select(c => c.Item.Value));
        foreach (var cell in layout.Cells)
        {
            var center = bitmap.GetPixel((int)(cell.CoverBox.X + (cell.CoverBox.Width / 2)), (int)(cell.CoverBox.Y + (cell.CoverBox.Height / 2)));
            Assert.True(Near(_colors[cell.Item], center), $"Cover of {cell.Item} not drawn in its cell.");
            Assert.All(cell.TitleLines, l => Assert.True(l.X >= cell.CoverBox.X - 10 && l.X + l.Width <= cell.CoverBox.Right + 10));
        }
    }

    [Fact]
    public async Task Png_without_the_pool_ignores_missing_covers_outside_its_scope()
    {
        var board = await BoardAsync(3, ranked: [(0, [1])], missingCover: [3]);

        await Assert.ThrowsAsync<ExportException>(() => BoardPngExporter.ExportAsync(board, _assets, _store, Out("all.png"), includeUnranked: true, replaceExisting: false, Ct));
        await BoardPngExporter.ExportAsync(board, _assets, _store, Out("tiers.png"), includeUnranked: false, replaceExisting: false, Ct);

        Assert.False(File.Exists(Out("all.png")));
        Assert.True(File.Exists(Out("tiers.png")));
        Assert.DoesNotContain(BoardPngExporter.Layout(board, includeUnranked: false).Rows, r => r.Label == "Unranked");
    }

    [Fact]
    public async Task Png_with_a_missing_or_damaged_in_scope_cover_names_the_shows_and_keeps_an_existing_file()
    {
        var board = await BoardAsync(3, ranked: [(0, [1, 2, 3])], missingCover: [2]);
        File.WriteAllBytes(_store.PathOf(_assets[board.Entries.Single(e => e.Item.Value == 3).PinnedCover!.Value]), [1, 2, 3]);
        File.WriteAllText(Out("keep.png"), "previous export");

        var error = await Assert.ThrowsAsync<ExportException>(() => BoardPngExporter.ExportAsync(board, _assets, _store, Out("keep.png"), includeUnranked: false, replaceExisting: true, Ct));

        Assert.Equal(["Title 2 [2] (no cover)", "Title 3 [3] (cover file missing or damaged)"], error.Affected);
        Assert.Equal("previous export", File.ReadAllText(Out("keep.png")));
        Assert.Single(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "out")));
    }

    [Fact]
    public async Task Png_refuses_an_existing_file_unless_replacement_was_chosen()
    {
        var board = await BoardAsync(1, ranked: [(0, [1])]);
        File.WriteAllText(Out("exists.png"), "old");

        await Assert.ThrowsAsync<ExportException>(() => BoardPngExporter.ExportAsync(board, _assets, _store, Out("exists.png"), false, replaceExisting: false, Ct));
        Assert.Equal("old", File.ReadAllText(Out("exists.png")));

        await BoardPngExporter.ExportAsync(board, _assets, _store, Out("exists.png"), false, replaceExisting: true, Ct);
        using var bitmap = SKBitmap.Decode(Out("exists.png"));
        Assert.NotNull(bitmap);
    }

    [Fact]
    public async Task Png_beyond_the_pixel_limit_fails_before_rendering()
    {
        var entries = Enumerable.Range(1, 1500).Select(i => new BoardEntry(new LiveChartId(i), $"Title {i}", null)).ToImmutableArray();
        var board = new BoardState(BoardId.New(), CollectionId.New(), "Huge", BoardOperations.DefaultTiers(), entries, ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>.Empty);

        var error = await Assert.ThrowsAsync<ExportException>(() => BoardPngExporter.ExportAsync(board, _assets, _store, Out("huge.png"), true, false, Ct));

        Assert.Contains("pixel limit", error.Message, StringComparison.Ordinal);
        Assert.Empty(error.Affected);
        Assert.False(File.Exists(Out("huge.png")));
    }

    [Fact]
    public void Layout_is_deterministic_and_wraps_long_latin_and_cjk_titles_without_losing_text()
    {
        const string cjk = "進撃の巨人 The Final Season 完結編 後編 ずっと続く長いタイトル";
        const string latin = "Mobile Suit Gundam: Iron-Blooded Orphans Urdr-Hunt -Path of the Little Challenger-";
        var entries = ImmutableArray.Create(new BoardEntry(new LiveChartId(1), cjk, null), new BoardEntry(new LiveChartId(2), latin, null));
        var board = new BoardState(BoardId.New(), CollectionId.New(), "Text", BoardOperations.DefaultTiers(), entries, ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>.Empty);

        var first = BoardPngExporter.Layout(board, includeUnranked: true);
        var second = BoardPngExporter.Layout(board, includeUnranked: true);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        foreach (var cell in first.Cells)
        {
            Assert.True(cell.TitleLines.Length > 1);
            Assert.All(cell.TitleLines, l => Assert.True(l.Width <= 132.5f, $"'{l.Text}' is {l.Width} wide."));
            Assert.Equal(cell.Title.Replace(" ", string.Empty, StringComparison.Ordinal), string.Concat(cell.TitleLines.Select(l => l.Text)).Replace(" ", string.Empty, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Selected_covers_follow_effective_order_with_safe_names_and_a_manifest()
    {
        var collection = await CollectionAsync(4, include: [1, 2, 4]);
        collection = CollectionOperations.Move(collection, new LiveChartId(4), 0);

        var manifest = await SelectedCoverExporter.ExportAsync(collection, e => $"Title {e.Item.Value}", _assets, _store, Out("covers"), Ct);

        Assert.Equal([4, 1, 2], manifest.Entries.Select(e => e.LiveChartId));
        Assert.Equal(["0001 Title 4 [4].jpg", "0002 Title 1 [1].jpg", "0003 Title 2 [2].jpg"], manifest.Entries.Select(e => e.File));
        Assert.Equal(["0001 Title 4 [4].jpg", "0002 Title 1 [1].jpg", "0003 Title 2 [2].jpg", "manifest.json"], Directory.EnumerateFiles(Out("covers")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var entry in manifest.Entries)
        {
            var asset = _assets[new AssetHash(entry.AssetHash)];
            Assert.Equal(File.ReadAllBytes(_store.PathOf(asset)), File.ReadAllBytes(Path.Combine(Out("covers"), entry.File)));
        }

        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Out("covers"), SelectedCoverExporter.ManifestFile)));
        Assert.Equal("animetierlist.selected-covers", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Selected_cover_export_writes_nothing_for_an_empty_scope_a_missing_cover_or_an_existing_folder()
    {
        var none = await CollectionAsync(2, include: []);
        var missing = await CollectionAsync(2, include: [1, 2], missingCover: [2]);
        Directory.CreateDirectory(Out("existing"));
        File.WriteAllText(Path.Combine(Out("existing"), "keep.txt"), "keep");

        await Assert.ThrowsAsync<ExportException>(() => SelectedCoverExporter.ExportAsync(none, e => "t", _assets, _store, Out("empty"), Ct));
        var error = await Assert.ThrowsAsync<ExportException>(() => SelectedCoverExporter.ExportAsync(missing, e => $"Title {e.Item.Value}", _assets, _store, Out("missing"), Ct));
        await Assert.ThrowsAsync<ExportException>(() => SelectedCoverExporter.ExportAsync(missing, e => "t", _assets, _store, Out("existing"), Ct));

        Assert.Equal(["Title 2 [2] (no cover)"], error.Affected);
        Assert.Equal(["existing"], Directory.EnumerateFileSystemEntries(Path.Combine(_root, "out")).Select(Path.GetFileName));
        Assert.Equal(["keep.txt"], Directory.EnumerateFiles(Out("existing")).Select(Path.GetFileName));
    }

    [Fact]
    public void File_names_sanitize_truncate_and_never_lose_the_order_id_or_extension()
    {
        var names = SelectedCoverExporter.FileNames(
        [
            (new LiveChartId(1), "CON", ".jpg"),
            (new LiveChartId(2), "What? <Season> 2: \"Part\" A/B\\C|D*. ", ".png"),
            (new LiveChartId(3), new string('x', 300), ".webp"),
            (new LiveChartId(4), "🎉🎉🎉", ".jpg"),
            (new LiveChartId(5), "...", ".jpg"),
        ], longestFolderPath: 40);

        Assert.Equal("0001 CON [1].jpg", names[0]);
        Assert.Equal("0002 What_ _Season_ 2_ _Part_ A_B_C_D_ [2].png", names[1]);
        Assert.Equal("0003 " + new string('x', SelectedCoverExporter.MaxTitleLength) + " [3].webp", names[2]);
        Assert.Equal("0004 🎉🎉🎉 [4].jpg", names[3]);
        Assert.Equal("0005 untitled [5].jpg", names[4]);

        var tight = SelectedCoverExporter.FileNames([(new LiveChartId(12345), "A fairly long title for a tight path", ".jpg")], longestFolderPath: 230);
        Assert.Equal(SelectedCoverExporter.MaxPathLength, Math.Max(230 + 1 + tight[0].Length, SelectedCoverExporter.MaxPathLength));
        Assert.EndsWith(" [12345].jpg", tight[0], StringComparison.Ordinal);
        Assert.Throws<ExportException>(() => SelectedCoverExporter.FileNames([(new LiveChartId(1), "x", ".jpg")], longestFolderPath: 250));
    }

    [Fact]
    public async Task Png_draws_big_red_copy_number_on_duplicated_entries_only()
    {
        var board = await BoardAsync(2, ranked: []);
        board = BoardOperations.DuplicateEntry(board, new LiveChartId(1));
        board = BoardOperations.MoveToTier(board, new BoardEntryKey(new LiveChartId(1), 1), board.Tiers[0].Id, 0);
        board = BoardOperations.MoveToTier(board, new BoardEntryKey(new LiveChartId(1), 2), board.Tiers[0].Id, 1);

        await BoardPngExporter.ExportAsync(board, _assets, _store, Out("duplicates.png"), includeUnranked: true, replaceExisting: false, Ct);

        var layout = BoardPngExporter.Layout(board, includeUnranked: true);
        Assert.Equal([1, 2], layout.Rows[0].Cells.Select(c => c.CopyNumber));
        using var bitmap = SKBitmap.Decode(Out("duplicates.png"));
        Assert.NotNull(bitmap);

        // Count vivid red pixels inside cover box of original (#1) vs duplicate (#2).
        static int CountRedPixels(SKBitmap bmp, LayoutRect box)
        {
            var count = 0;
            for (var y = (int)box.Y; y < (int)box.Bottom; y++)
            {
                for (var x = (int)box.X; x < (int)box.Right; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.Red >= 200 && p.Green <= 70 && p.Blue <= 70)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        var originalRed = CountRedPixels(bitmap, layout.Rows[0].Cells[0].CoverBox);
        var duplicateRed = CountRedPixels(bitmap, layout.Rows[0].Cells[1].CoverBox);
        Assert.Equal(0, originalRed);
        Assert.True(duplicateRed > 200, $"Expected prominent red copy number on duplicate cover, found only {duplicateRed} red pixels.");
    }

    private static bool Near(SKColor expected, SKColor actual) =>
        Math.Abs(expected.Red - actual.Red) < 24 && Math.Abs(expected.Green - actual.Green) < 24 && Math.Abs(expected.Blue - actual.Blue) < 24;

    private async Task<AssetRecord> InstallCoverAsync(int id)
    {
        var color = new SKColor((byte)(40 * id % 256), (byte)(255 - (30 * id % 256)), (byte)(90 + (20 * id % 120)));
        _colors[new LiveChartId(id)] = color;
        using var bitmap = new SKBitmap(175, 250);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var bytes = data.ToArray();
        var accepted = Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, ImageLimits.Default));
        var record = await _store.InstallAsync(bytes, accepted.Asset, Ct);
        _assets[record.Hash] = record;
        return record;
    }

    private async Task<BoardState> BoardAsync(int count, (int Tier, int[] Items)[] ranked, int[]? missingCover = null)
    {
        var entries = ImmutableArray.CreateBuilder<BoardEntry>();
        for (var id = 1; id <= count; id++)
        {
            var cover = missingCover?.Contains(id) == true ? (AssetHash?)null : (await InstallCoverAsync(id)).Hash;
            entries.Add(new BoardEntry(new LiveChartId(id), $"Title {id}", cover));
        }

        var tiers = BoardOperations.DefaultTiers();
        var placements = ranked.ToImmutableDictionary(r => tiers[r.Tier].Id, r => r.Items.Select(i => new BoardEntryKey(new LiveChartId(i))).ToImmutableArray());
        return new BoardState(BoardId.New(), CollectionId.New(), "My ranking", tiers, entries.ToImmutable(), placements);
    }

    private async Task<CollectionState> CollectionAsync(int count, int[] include, int[]? missingCover = null)
    {
        var occurrences = Enumerable.Range(1, count).Select(i => new SourceOccurrence(i - 1, new LiveChartId(i), new SourceTitles($"Title {i}", null, null, null),
            [new ImageCandidate("large", new Uri($"https://u.livechart.me/anime/{i}/large.jpg"))])).ToImmutableArray();
        var snapshot = new SourceSnapshot(SnapshotId.New(), Context,
            [new SeasonCapture(new SeasonKey(2025, Season.Summer), CatalogueCategory.Tv, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "test", occurrences)]);
        var collection = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Summer 2025", snapshot), included: false);
        collection = CollectionOperations.SetIncluded(collection, include.Select(i => new LiveChartId(i)).ToList(), true);
        foreach (var entry in collection.Entries.ToList())
        {
            if (missingCover?.Contains(entry.Item.Value) == true)
            {
                continue;
            }

            var asset = await InstallCoverAsync(entry.Item.Value);
            var url = snapshot.Occurrence(entry.AcceptedOccurrence).ImageCandidates[0].Url;
            Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, entry.Item, new CoverProvenance(entry.AcceptedOccurrence, url, url, "Accept: image/jpeg", asset.Hash), out collection));
        }

        return collection;
    }
}
