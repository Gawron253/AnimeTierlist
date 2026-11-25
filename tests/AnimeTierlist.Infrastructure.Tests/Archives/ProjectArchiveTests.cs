using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Archives;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace AnimeTierlist.Infrastructure.Tests.Archives;

/// <summary>Archive cases: consistent pruned export, same-release validation, and hostile import inputs.</summary>
public sealed class ProjectArchiveTests : IDisposable
{
    private static readonly LiveChartId A = new(301);
    private static readonly LiveChartId B = new(302);
    private static readonly LiveChartId C = new(303);
    private static readonly LiveChartId D = new(304);
    private static readonly AcquisitionContext Summer = new(2025, Season.Summer, CatalogueCategory.Tv);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));

    public ProjectArchiveTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Source => Path.Combine(_root, "source");

    private string Archive => Path.Combine(_root, "out", "project.animerank");

    private string Imported => Path.Combine(_root, "imported", "project");

    [Fact]
    public async Task Export_and_import_round_trip_the_saved_revision_with_history_pruned_from_the_archive_only()
    {
        var sample = await CreateSourceAsync();
        await ExportAsync();

        var names = Entries(Archive).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { sample.CoverA.RelativePath, sample.CoverB.RelativePath, "manifest.json", "project.sqlite" }.Order(StringComparer.Ordinal), names);
        var header = Entries(Archive).Single(e => e.Name == "project.sqlite").Bytes;
        Assert.Equal((1, 1), (header[18], header[19]));

        await ProjectArchiveImporter.ImportAsync(Archive, Imported, ArchiveLimits.Default, Ct);

        await using var imported = await SqliteProjectStore.OpenAsync(Imported, Ct);
        var data = await imported.LoadAsync(Ct);
        Assert.Equal([sample.Applied.Id], data.Snapshots.Keys);
        Assert.Equal(4, data.Snapshots[sample.Applied.Id].OccurrenceCount);
        Assert.Equal(new[] { sample.CoverA.Hash, sample.CoverB.Hash }.OrderBy(h => h.Value, StringComparer.Ordinal), data.Assets.Keys.OrderBy(h => h.Value, StringComparer.Ordinal));
        Assert.Equal(Describe(sample.Collection), Describe(Assert.Single(data.Collections)));
        Assert.Equal(Describe(sample.Board), Describe(Assert.Single(data.Boards)));
        var originals = new AssetStore(Imported);
        Assert.Equal(AssetAvailability.Available, await originals.CheckAsync(sample.CoverA, Ct));
        Assert.Equal(AssetAvailability.Available, await originals.CheckAsync(sample.CoverB, Ct));
        Assert.True(Directory.Exists(Path.Combine(Imported, "cache")));

        // The live project keeps its history, unused original, and orphan-only item.
        await using var source = await SqliteProjectStore.OpenAsync(Source, Ct);
        var live = await source.LoadAsync(Ct);
        Assert.Equal(2, live.Snapshots.Count);
        Assert.Equal(3, live.Assets.Count);
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task Export_refuses_an_existing_destination_unless_replacement_is_chosen()
    {
        await CreateSourceAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
        await File.WriteAllTextAsync(Archive, "previous", Ct);

        await Assert.ThrowsAsync<ProjectArchiveException>(() => ExportAsync());
        Assert.Equal("previous", await File.ReadAllTextAsync(Archive, Ct));

        await ExportAsync(replace: true);
        await ProjectArchiveImporter.ValidateAsync(Archive, ArchiveLimits.Default, Ct);
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task Failed_finalization_keeps_the_previous_file_and_removes_temporary_output()
    {
        await CreateSourceAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
        await File.WriteAllTextAsync(Archive, "previous", Ct);
        File.SetAttributes(Archive, FileAttributes.ReadOnly);

        await Assert.ThrowsAsync<ProjectArchiveException>(() => ExportAsync(replace: true));

        Assert.Equal("previous", await File.ReadAllTextAsync(Archive, Ct));
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task Broken_referenced_originals_block_export_and_are_reported()
    {
        var sample = await CreateSourceAsync();
        File.Delete(new AssetStore(Source).PathOf(sample.CoverB));

        var error = await Assert.ThrowsAsync<ProjectArchiveException>(() => ExportAsync());

        Assert.Equal([sample.CoverB.Hash], error.BrokenAssets);
        Assert.False(File.Exists(Archive));
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task Import_never_overwrites_an_existing_folder()
    {
        await CreateSourceAsync();
        await ExportAsync();
        Directory.CreateDirectory(Imported);
        await File.WriteAllTextAsync(Path.Combine(Imported, "keep.txt"), "mine", Ct);

        await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ImportAsync(Archive, Imported, ArchiveLimits.Default, Ct));

        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(Imported).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Path_traversal_entry_is_rejected() =>
        await AssertRejectedAsync("unexpected entry", entries => entries.Add(("../escape.txt", "x"u8.ToArray())));

    [Fact]
    public async Task Directory_entry_is_rejected() =>
        await AssertRejectedAsync("unexpected entry", entries => entries.Add(("assets/", [])));

    [Fact]
    public async Task Duplicate_entry_is_rejected() =>
        await AssertRejectedAsync("duplicate entry", entries => entries.Add(entries.Single(e => e.Name == "manifest.json")));

    [Fact]
    public async Task Database_sidecar_is_rejected() =>
        await AssertRejectedAsync("unexpected entry", entries => entries.Add(("project.sqlite-wal", new byte[32])));

    [Fact]
    public async Task Noncanonical_asset_path_spelling_is_rejected() =>
        await AssertRejectedAsync("unexpected entry", entries =>
        {
            var index = entries.FindIndex(e => e.Name.StartsWith("assets/", StringComparison.Ordinal));
            entries[index] = (entries[index].Name.ToUpperInvariant().Replace("ASSETS/", "assets/", StringComparison.Ordinal), entries[index].Bytes);
        });

    [Fact]
    public async Task Changed_original_bytes_are_rejected() =>
        await AssertRejectedAsync("does not match its recorded hash", entries =>
        {
            var index = entries.FindIndex(e => e.Name.StartsWith("assets/", StringComparison.Ordinal));
            var bytes = entries[index].Bytes.ToArray();
            bytes[^3] ^= 0xFF;
            entries[index] = (entries[index].Name, bytes);
        });

    [Fact]
    public async Task Original_missing_from_the_manifest_is_rejected() =>
        await AssertRejectedAsync("manifest does not list", entries => EditManifest(entries, m => m["assets"]!.AsArray().RemoveAt(0)));

    [Fact]
    public async Task Unknown_manifest_property_is_rejected() =>
        await AssertRejectedAsync("'extra'", entries => EditManifest(entries, m => m["extra"] = true));

    [Fact]
    public async Task Newer_archive_version_is_rejected_clearly()
    {
        var error = await AssertRejectedAsync("newer release", entries => EditManifest(entries, m => m["formatVersion"] = 2));

        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_ahead_log_database_header_is_rejected() =>
        await AssertRejectedAsync("write-ahead logging", entries => EditDatabaseBytes(entries, bytes => bytes[18] = bytes[19] = 2));

    [Fact]
    public async Task Unexpected_schema_object_is_rejected() =>
        await AssertRejectedAsync("trigger planted", entries => EditDatabase(entries, "CREATE TRIGGER planted AFTER INSERT ON items BEGIN SELECT 1; END;"));

    /// <summary>
    /// The schema text in a database follows the line endings of the checkout that built the release (the literal
    /// in ProjectSchema), so an archive from a CRLF build must import into an LF build and the reverse.
    /// </summary>
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task Schema_text_line_endings_from_another_build_still_import(string lineEnding)
    {
        await CreateSourceAsync();
        await ExportAsync();
        var entries = Entries(Archive);
        const string crlf = "char(13) || char(10)";
        var target = lineEnding == "\n" ? "char(10)" : crlf;
        EditDatabase(entries, $"PRAGMA writable_schema = ON; UPDATE sqlite_schema SET sql = replace(replace(sql, {crlf}, char(10)), char(10), {target}) WHERE sql IS NOT NULL; PRAGMA writable_schema = OFF;");
        var rewritten = Path.Combine(_root, "out", "line-endings.animerank");
        WriteZip(rewritten, entries);

        await ProjectArchiveImporter.ImportAsync(rewritten, Imported, ArchiveLimits.Default, Ct);

        await using var imported = await SqliteProjectStore.OpenAsync(Imported, Ct);
        Assert.Single((await imported.LoadAsync(Ct)).Collections);
    }

    [Fact]
    public async Task Original_with_mismatched_recorded_dimensions_is_rejected() =>
        await AssertRejectedAsync("does not match its recorded hash, format, or dimensions", entries => EditDatabase(entries, "UPDATE assets SET width = width + 1;"));

    [Fact]
    public async Task Unreferenced_item_is_rejected() =>
        await AssertRejectedAsync("unreferenced items", entries => EditDatabase(entries, "INSERT INTO items (livechart_id) VALUES (999999);"));

    [Fact]
    public async Task Source_missing_flag_that_contradicts_the_applied_snapshot_is_rejected() =>
        await AssertRejectedAsync("source-missing exactly", entries => EditDatabase(entries, $"UPDATE collection_entries SET source_missing = 1 WHERE livechart_id = {C.Value};"));

    [Fact]
    public async Task Cover_provenance_outside_its_cited_occurrence_is_rejected() =>
        await AssertRejectedAsync("does not belong to its cited source occurrence", entries => EditDatabase(entries, "UPDATE collection_entries SET cover_requested_url = 'https://u.livechart.me/elsewhere.jpg' WHERE cover_asset_hash IS NOT NULL;"));

    [Theory]
    [InlineData(unchecked((int)0xA1FF0000))]
    [InlineData(0x420)]
    public async Task Link_entry_is_rejected(int externalAttributes)
    {
        await CreateSourceAsync();
        await ExportAsync();
        var tampered = Path.Combine(_root, "out", "link.animerank");
        File.Copy(Archive, tampered);
        using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update))
        {
            var entry = zip.Entries.First(e => e.FullName.StartsWith("assets/", StringComparison.Ordinal));
            entry.ExternalAttributes = externalAttributes;
        }

        var error = await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ImportAsync(tampered, Imported, ArchiveLimits.Default, Ct));

        Assert.Contains("link or special file", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Imported));
    }

    [Fact]
    public void Default_limits_match_the_specification()
    {
        Assert.Equal(20_000, ArchiveLimits.Default.MaxEntries);
        Assert.Equal(1L << 30, ArchiveLimits.Default.MaxTotalBytes);
        Assert.Equal(20L << 20, ArchiveLimits.Default.Image.MaxCompressedBytes);
        Assert.Equal(40_000_000, ArchiveLimits.Default.Image.MaxDecodedPixels);
    }

    [Fact]
    public async Task Expanded_size_and_entry_limits_are_exact_at_the_boundary()
    {
        await CreateSourceAsync();
        await ExportAsync();
        var entries = Entries(Archive);
        var total = entries.Sum(e => (long)e.Bytes.Length);

        await ProjectArchiveImporter.ValidateAsync(Archive, ArchiveLimits.Default with { MaxTotalBytes = total, MaxEntries = entries.Count }, Ct);
        var overSize = await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ValidateAsync(Archive, ArchiveLimits.Default with { MaxTotalBytes = total - 1 }, Ct));
        var overCount = await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ValidateAsync(Archive, ArchiveLimits.Default with { MaxEntries = entries.Count - 1 }, Ct));

        Assert.Contains("beyond the allowed size", overSize.Message, StringComparison.Ordinal);
        Assert.Contains("entries; at most", overCount.Message, StringComparison.Ordinal);
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task Entry_count_limit_is_shared_by_import() =>
        await AssertRejectedAsync("at most 3 are allowed", _ => { }, ArchiveLimits.Default with { MaxEntries = 3 });

    [Fact]
    public async Task Database_size_limit_is_shared_by_import() =>
        await AssertRejectedAsync("'project.sqlite' is larger than allowed", _ => { }, ArchiveLimits.Default with { MaxDatabaseBytes = 4096 });

    [Fact]
    public async Task Export_preflight_uses_the_same_limits()
    {
        await CreateSourceAsync();

        await Assert.ThrowsAsync<ProjectArchiveException>(() => ExportAsync(limits: ArchiveLimits.Default with { MaxEntries = 3 }));

        Assert.False(File.Exists(Archive));
        AssertNoStagingLeftovers();
    }

    [Fact]
    public async Task Non_zip_input_is_rejected()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
        await File.WriteAllTextAsync(Archive, "not a zip", Ct);

        await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ImportAsync(Archive, Imported, ArchiveLimits.Default, Ct));

        Assert.False(Directory.Exists(Imported));
    }

    private async Task<ProjectArchiveException> AssertRejectedAsync(string reason, Action<List<(string Name, byte[] Bytes)>> tamper, ArchiveLimits? limits = null)
    {
        await CreateSourceAsync();
        await ExportAsync();
        var entries = Entries(Archive);
        tamper(entries);
        var tampered = Path.Combine(_root, "out", "tampered.animerank");
        WriteZip(tampered, entries);

        var error = await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ImportAsync(tampered, Imported, limits ?? ArchiveLimits.Default, Ct));

        Assert.False(Directory.Exists(Imported));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        AssertNoStagingLeftovers();
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        return error;
    }

    private async Task ExportAsync(bool replace = false, ArchiveLimits? limits = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
        await using var store = await SqliteProjectStore.OpenAsync(Source, Ct);
        await ProjectArchiveExporter.ExportAsync(store, Archive, replace, limits ?? ArchiveLimits.Default, Ct);
    }

    private void AssertNoStagingLeftovers()
    {
        var leftovers = Directory.EnumerateFileSystemEntries(_root, ".animerank-*", SearchOption.AllDirectories).ToList();
        Assert.Empty(leftovers);
    }

    /// <summary>
    /// A saved project with an applied snapshot containing a repeated ID, an older unreferenced snapshot whose only
    /// item appears nowhere else, two selected covers (one also pinned on a board), and one unreferenced original.
    /// </summary>
    private async Task<Sample> CreateSourceAsync()
    {
        var originals = new AssetStore(Source);
        await using var store = await SqliteProjectStore.CreateAsync(Source, Ct);
        var coverA = await InstallAsync(originals, SKEncodedImageFormat.Png, 35, 50);
        var coverB = await InstallAsync(originals, SKEncodedImageFormat.Jpeg, 35, 50);
        var unused = await InstallAsync(originals, SKEncodedImageFormat.Webp, 20, 30);

        var old = Snapshot(D);
        var applied = Snapshot(A, B, C, A);
        var collection = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Summer 2025", applied), true);
        collection = Attach(Attach(collection, applied, A, coverA), applied, B, coverB);
        collection = CollectionOperations.SetIncluded(collection, [B], false);
        var board = BoardOperations.Create(BoardId.New(), "Board", collection,
            e => TitleResolver.Resolve(applied.Occurrence(e.AcceptedOccurrence).Titles, collection.Language, e.TitleOverride), _ => true, BoardOperations.DefaultTiers());
        board = BoardOperations.MoveToTier(board, A, board.Tiers[0].Id, 0);

        await store.CommitAsync(new ProjectChange
        {
            AddSnapshots = [old, applied],
            AddAssets = [coverA, coverB, unused],
            PutCollections = [collection],
            PutBoards = [board],
        }, Ct);
        return new Sample(applied, collection, board, coverA, coverB);
    }

    private static CollectionState Attach(CollectionState collection, SourceSnapshot snapshot, LiveChartId item, AssetRecord asset)
    {
        var occurrence = collection.Entry(item).AcceptedOccurrence;
        var url = snapshot.Occurrence(occurrence).ImageCandidates[0].Url;
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, item, new CoverProvenance(occurrence, url, url, "Accept: image/jpeg,image/png", asset.Hash), out var updated));
        return updated;
    }

    private static async Task<AssetRecord> InstallAsync(AssetStore store, SKEncodedImageFormat format, int width, int height)
    {
        var bytes = Encode(format, width, height);
        var asset = Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(bytes, ImageLimits.Default)).Asset;
        return await store.InstallAsync(bytes, asset, Ct);
    }

    private static SourceSnapshot Snapshot(params LiveChartId[] ids) =>
        new(SnapshotId.New(), Summer,
        [
            new SeasonCapture(new SeasonKey(2025, Season.Summer), CatalogueCategory.Tv, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "test",
                ids.Select((id, i) => new SourceOccurrence(i, id, new SourceTitles($"Title {id}", null, $"Romaji {id}", null),
                    [new ImageCandidate("large", new Uri($"https://u.livechart.me/anime/{id}/poster_image/x.webp/large.jpg"))])).ToImmutableArray()),
        ]);

    private static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor((byte)width, (byte)height, (byte)format));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static List<(string Name, byte[] Bytes)> Entries(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (e.FullName, copy.ToArray());
        }).ToList();
    }

    private static void WriteZip(string path, List<(string Name, byte[] Bytes)> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    }

    private static void EditManifest(List<(string Name, byte[] Bytes)> entries, Action<JsonObject> edit)
    {
        var index = entries.FindIndex(e => e.Name == "manifest.json");
        var manifest = JsonNode.Parse(entries[index].Bytes)!.AsObject();
        edit(manifest);
        entries[index] = ("manifest.json", System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    private static void EditDatabaseBytes(List<(string Name, byte[] Bytes)> entries, Action<byte[]> edit)
    {
        var index = entries.FindIndex(e => e.Name == "project.sqlite");
        var bytes = entries[index].Bytes.ToArray();
        edit(bytes);
        entries[index] = ("project.sqlite", bytes);
    }

    private void EditDatabase(List<(string Name, byte[] Bytes)> entries, string sql)
    {
        var index = entries.FindIndex(e => e.Name == "project.sqlite");
        var file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        File.WriteAllBytes(file, entries[index].Bytes);
        using (var connection = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        entries[index] = ("project.sqlite", File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Export_and_import_preserve_sidebar_order_duplicate_board_entries_and_source_details()
    {
        var details = new SourceDetails(
            "8.72 out of 10",
            "2025-07-05",
            "TV · 12 eps",
            "Archive synopsis.",
            ["CloverWorks"],
            ["Romance", "Slice of Life"],
            new Uri("https://anilist.co/anime/185660"),
            new Uri("https://myanimelist.net/anime/60543"));
        var extrasA = new AnimeExtraDetails(
            A,
            new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.Zero),
            details,
            [
                new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=op1"), AnimeVideoKind.Opening, "1:31", "JA", null, false),
                new AnimeVideo("ED", new Uri("https://www.youtube.com/watch?v=ed1"), AnimeVideoKind.Ending, "1:31", "JA", "EN", false),
            ]);
        var oldSnapshot = Snapshot(D);
        var extrasD = new AnimeExtraDetails(
            D,
            new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.Zero),
            SourceDetails.Empty,
            [new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=pruned"), AnimeVideoKind.Opening)]);
        var snapshot = new SourceSnapshot(
            SnapshotId.New(),
            Summer,
            [
                new SeasonCapture(
                    new SeasonKey(2025, Season.Summer),
                    CatalogueCategory.Tv,
                    new Uri("https://www.livechart.me/summer-2025/tv"),
                    DateTimeOffset.UnixEpoch,
                    "test",
                    [
                        new SourceOccurrence(0, A, new SourceTitles("Show A", "Show A EN", "Show A Romaji", "ショーA"), [], details),
                        new SourceOccurrence(1, B, new SourceTitles("Show B", null, "Show B Romaji", null), []),
                    ]),
            ]);
        var col1 = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "First", snapshot), true);
        var col2 = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Second", snapshot), true);
        var board1 = BoardOperations.Create(BoardId.New(), "Board 1", col1, e => new ResolvedTitle($"T {e.Item}", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());
        board1 = BoardOperations.DuplicateEntry(board1, A);
        board1 = BoardOperations.MoveToTier(board1, new BoardEntryKey(A, 2), board1.Tiers[0].Id, 0);
        var board2 = BoardOperations.Create(BoardId.New(), "Board 2", col1, e => new ResolvedTitle($"T {e.Item}", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());

        await using (var store = await SqliteProjectStore.CreateAsync(Source, Ct))
        {
            await store.CommitAsync(new ProjectChange
            {
                AddSnapshots = [oldSnapshot, snapshot],
                PutCollections = [col1, col2],
                PutBoards = [board1, board2],
                CollectionOrder = [col2.Id, col1.Id],
                BoardOrder = [board2.Id, board1.Id],
                PutItemExtras = [extrasA, extrasD],
            }, Ct);
        }

        await ExportAsync();
        await ProjectArchiveImporter.ImportAsync(Archive, Imported, ArchiveLimits.Default, Ct);

        await using var imported = await SqliteProjectStore.OpenAsync(Imported, Ct);
        var data = await imported.LoadAsync(Ct);
        Assert.Equal([col2.Id, col1.Id], data.Collections.Select(c => c.Id));
        Assert.Equal([board2.Id, board1.Id], data.Boards.Select(b => b.Id));
        Assert.Equal(details, data.Snapshots[snapshot.Id].Captures[0].Occurrences[0].Details);
        Assert.Equal(extrasA, data.ItemExtras[A]);
        Assert.False(data.ItemExtras.ContainsKey(D)); // Pruned along with unreferenced older snapshot item D.
        var loadedBoard1 = data.Boards.Single(b => b.Id == board1.Id);
        Assert.Equal([new BoardEntryKey(A, 1), new BoardEntryKey(A, 2), new BoardEntryKey(B, 1)], loadedBoard1.Entries.Select(e => e.Key));
        Assert.Equal([new BoardEntryKey(A, 2)], loadedBoard1.Placements[loadedBoard1.Tiers[0].Id]);
    }

    [Fact]
    public async Task Import_upgrades_valid_version_1_archive_and_rejects_tampered_version_1_schema()
    {
        var snapshotId = SnapshotId.New();
        var colZeta = CollectionId.New();
        var colAlpha = CollectionId.New();
        var boardZeta = BoardId.New();
        var boardAlpha = BoardId.New();
        var tierId = TierId.New();

        var coverBytes = Encode(SKEncodedImageFormat.Jpeg, 175, 250);
        var cover = Assert.IsType<ImageValidation.Accepted>(ImageValidator.Validate(coverBytes, ImageLimits.Default)).Asset;
        var coverUrl = "https://u.livechart.me/anime/101/poster_image/x.webp/large.jpg";

        var v1DbPath = Path.Combine(_root, "v1-project.sqlite");
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = v1DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true }.ToString()))
        {
            raw.Open();
            SqliteProjectStore.Execute(raw, null, "PRAGMA journal_mode = DELETE;");
            SqliteProjectStore.Execute(raw, null, ProjectSchema.CreateV1);
            SqliteProjectStore.Execute(raw, null, """
                INSERT INTO project_meta VALUES ('application', 'AnimeTierlist.Project');
                INSERT INTO items VALUES (101);
                PRAGMA user_version = 1;
                """);
            SqliteProjectStore.Execute(raw, null, "INSERT INTO snapshots VALUES ($s, 2025, 2, 0)", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO season_captures VALUES ($s, 2025, 2, 0, 'https://www.livechart.me/summer-2025/tv', '2026-11-17T22:00:00Z', 'v1')", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO occurrences VALUES ($s, 2025, 2, 0, 101, 'Legacy Show', NULL, 'Legacy Show', NULL)", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO image_candidates VALUES ($s, 2025, 2, 0, 0, 'large', $u)", ("$s", snapshotId.ToString()), ("$u", coverUrl));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO assets VALUES ($h, 'jpg', $w, $ht, $len, $p)",
                ("$h", cover.Hash.Value), ("$w", cover.Width), ("$ht", cover.Height), ("$len", cover.ByteLength), ("$p", cover.RelativePath));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'Zeta', 2025, 2, 0, $s, 0)", ("$c", colZeta.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'Alpha', 2025, 2, 0, $s, 0)", ("$c", colAlpha.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collection_entries VALUES ($c, 101, 0, 1, 0, NULL, $s, 2025, 2, 0, $h, $s, 2025, 2, 0, $u, $u, 'Accept: image/jpeg')",
                ("$c", colZeta.ToString()), ("$s", snapshotId.ToString()), ("$h", cover.Hash.Value), ("$u", coverUrl));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO boards VALUES ($b, $c, 'Zeta Board')", ("$b", boardZeta.ToString()), ("$c", colZeta.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO boards VALUES ($b, $c, 'Alpha Board')", ("$b", boardAlpha.ToString()), ("$c", colZeta.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO tiers VALUES ($b, $t, 0, 'S', '#FF7F7F')", ("$b", boardZeta.ToString()), ("$t", tierId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO board_entries VALUES ($b, 101, 0, 'Legacy Show', $h, $t, 0)", ("$b", boardZeta.ToString()), ("$h", cover.Hash.Value), ("$t", tierId.ToString()));
        }

        SqliteConnection.ClearAllPools();
        var manifest = new ArchiveManifest(
            ProjectArchive.Format,
            ProjectArchive.FormatVersion,
            SchemaVersion: 1,
            [new ArchiveManifestAsset(cover.RelativePath, cover.Hash.Value, cover.ByteLength)]);
        var v1Entries = new List<(string Name, byte[] Bytes)>
        {
            (ProjectArchive.ManifestEntry, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectArchive.JsonOptions)),
            (ProjectArchive.DatabaseEntry, await File.ReadAllBytesAsync(v1DbPath, Ct)),
            (cover.RelativePath, coverBytes),
        };

        var v1Archive = Path.Combine(_root, "v1-backup.animerank");
        WriteZip(v1Archive, v1Entries);

        // 1. Valid v1 archive validates, imports, upgrades in staging, and preserves V1 alphabetical sidebar order.
        await ProjectArchiveImporter.ValidateAsync(v1Archive, ArchiveLimits.Default, Ct);
        await ProjectArchiveImporter.ImportAsync(v1Archive, Imported, ArchiveLimits.Default, Ct);
        await using (var opened = await SqliteProjectStore.OpenAsync(Imported, Ct))
        {
            var data = await opened.LoadAsync(Ct);
            Assert.Equal([colAlpha, colZeta], data.Collections.Select(c => c.Id));
            Assert.Equal([boardAlpha, boardZeta], data.Boards.Select(b => b.Id));
            Assert.Equal([new BoardEntryKey(new LiveChartId(101), 1)], data.Boards.Single(b => b.Id == boardZeta).Placements[tierId]);
        }

        // 2. Tampered v1 archive with an unexpected trigger is rejected before upgrade runs.
        var tamperedEntries = v1Entries.Select(e => (e.Name, e.Bytes.ToArray())).ToList();
        EditDatabase(tamperedEntries, "CREATE TRIGGER evil AFTER INSERT ON items BEGIN SELECT 1; END;");
        var tamperedArchive = Path.Combine(_root, "v1-tampered.animerank");
        WriteZip(tamperedArchive, tamperedEntries);
        var tamperedDestination = Path.Combine(_root, "imported-tampered");
        var error = await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ImportAsync(tamperedArchive, tamperedDestination, ArchiveLimits.Default, Ct));
        Assert.Contains("exact schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(tamperedDestination));
    }

    [Fact]
    public async Task Import_upgrades_valid_version_2_archive_and_rejects_tampered_version_2_schema()
    {
        var snapshotId = SnapshotId.New();
        var colId = CollectionId.New();
        var boardId = BoardId.New();
        var tierId = TierId.New();

        var v2DbPath = Path.Combine(_root, "v2-project.sqlite");
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = v2DbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true }.ToString()))
        {
            raw.Open();
            SqliteProjectStore.Execute(raw, null, "PRAGMA journal_mode = DELETE;");
            SqliteProjectStore.Execute(raw, null, ProjectSchema.CreateV2);
            SqliteProjectStore.Execute(raw, null, """
                INSERT INTO project_meta VALUES ('application', 'AnimeTierlist.Project');
                INSERT INTO items VALUES (101);
                PRAGMA user_version = 2;
                """);
            SqliteProjectStore.Execute(raw, null, "INSERT INTO snapshots VALUES ($s, 2025, 2, 0)", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO season_captures VALUES ($s, 2025, 2, 0, 'https://www.livechart.me/summer-2025/tv', '2026-11-17T22:00:00Z', 'v2')", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO occurrences VALUES ($s, 2025, 2, 0, 101, 'V2 Archive Show', NULL, 'V2 Archive Show', NULL, '8.5', '2025-07-03', 'TV · 12 eps', 'V2 synopsis.', '[\"HORNETS\"]', '[\"Fantasy\"]')", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'V2 Col', 2025, 2, 0, $s, 0, 0)", ("$c", colId.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collection_entries VALUES ($c, 101, 0, 1, 0, NULL, $s, 2025, 2, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)", ("$c", colId.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO boards VALUES ($b, $c, 'V2 Board', 0)", ("$b", boardId.ToString()), ("$c", colId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO tiers VALUES ($b, $t, 0, 'S', '#FF7F7F')", ("$b", boardId.ToString()), ("$t", tierId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO board_entries VALUES ($b, 101, 1, 0, 'V2 Archive Show', NULL, $t, 0)", ("$b", boardId.ToString()), ("$t", tierId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO board_entries VALUES ($b, 101, 2, 1, 'V2 Archive Show', NULL, NULL, NULL)", ("$b", boardId.ToString()));
        }

        SqliteConnection.ClearAllPools();
        var manifest = new ArchiveManifest(ProjectArchive.Format, ProjectArchive.FormatVersion, SchemaVersion: 2, []);
        var v2Entries = new List<(string Name, byte[] Bytes)>
        {
            (ProjectArchive.ManifestEntry, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectArchive.JsonOptions)),
            (ProjectArchive.DatabaseEntry, await File.ReadAllBytesAsync(v2DbPath, Ct)),
        };

        var v2Archive = Path.Combine(_root, "v2-backup.animerank");
        WriteZip(v2Archive, v2Entries);

        await ProjectArchiveImporter.ImportAsync(v2Archive, Imported, ArchiveLimits.Default, Ct);
        await using (var opened = await SqliteProjectStore.OpenAsync(Imported, Ct))
        {
            var data = await opened.LoadAsync(Ct);
            Assert.Equal("V2 synopsis.", data.Snapshots[snapshotId].Captures[0].Occurrences[0].Details.Synopsis);
            Assert.Equal(2, data.Boards.Single().Entries.Length);
        }

        var tamperedEntries = v2Entries.Select(e => (e.Name, e.Bytes.ToArray())).ToList();
        EditDatabase(tamperedEntries, "CREATE VIEW evil AS SELECT 1;");
        var tamperedArchive = Path.Combine(_root, "v2-tampered.animerank");
        WriteZip(tamperedArchive, tamperedEntries);
        var tamperedDestination = Path.Combine(_root, "imported-v2-tampered");
        var error = await Assert.ThrowsAsync<ProjectArchiveException>(() => ProjectArchiveImporter.ImportAsync(tamperedArchive, tamperedDestination, ArchiveLimits.Default, Ct));
        Assert.Contains("exact schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(tamperedDestination));
    }

    [Fact]
    public async Task Archive_with_videos_when_videos_fetched_is_false_is_rejected()
    {
        await AssertRejectedAsync(
            "videos_fetched is false",
            entries => EditDatabase(entries, $"""
                INSERT INTO item_extras (livechart_id, retrieved_at, videos_fetched, profile_fetched, rating)
                VALUES ({A.Value}, '2026-11-24T21:00:00Z', 0, 1, '8.5');
                INSERT INTO item_videos (livechart_id, ordinal, title, url, kind, has_spoilers)
                VALUES ({A.Value}, 0, 'OP', 'https://www.youtube.com/watch?v=abc', 0, 0);
                """));
    }

    [Fact]
    public async Task Archive_with_profile_details_when_profile_fetched_is_false_is_rejected()
    {
        await AssertRejectedAsync(
            "profile_fetched is false",
            entries => EditDatabase(entries, $"""
                INSERT INTO item_extras (livechart_id, retrieved_at, videos_fetched, profile_fetched, rating)
                VALUES ({B.Value}, '2026-11-24T21:00:00Z', 1, 0, '8.5');
                """));
    }

    private static string Describe(CollectionState c) =>
        $"{c.Id}|{c.Name}|{c.Context}|{c.AppliedSnapshot}|{c.Language}|" + string.Join(";", c.Entries);

    private static string Describe(BoardState b) =>
        $"{b.Id}|{b.Collection}|{b.Name}|" + string.Join(";", b.Tiers) + "|" + string.Join(";", b.Entries) + "|" +
        string.Join(";", b.Tiers.Select(t => t.Id + "=" + string.Join(",", b.Placements[t.Id])));

    private sealed record Sample(SourceSnapshot Applied, CollectionState Collection, BoardState Board, AssetRecord CoverA, AssetRecord CoverB);
}
