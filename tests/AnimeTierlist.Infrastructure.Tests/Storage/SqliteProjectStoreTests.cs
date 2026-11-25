using System.Collections.Immutable;
using System.Security.Cryptography;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Tests.Storage;

/// <summary>Storage cases against real temporary SQLite databases.</summary>
public sealed class SqliteProjectStoreTests : IDisposable
{
    private static readonly LiveChartId A = new(101);
    private static readonly LiveChartId B = new(102);
    private static readonly LiveChartId C = new(103);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "animetierlist-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ProjectDir => Path.Combine(_root, "project");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Saved_project_round_trips_after_reopen()
    {
        var (snapshot, collection, board, asset) = SampleProject();
        await using (var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct))
        {
            await store.CommitAsync(new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset], PutCollections = [collection], PutBoards = [board] }, Ct);
        }

        await using var reopened = await SqliteProjectStore.OpenAsync(ProjectDir, Ct);
        var data = await reopened.LoadAsync(Ct);

        var loadedSnapshot = data.Snapshots[snapshot.Id];
        Assert.Equal(Describe(snapshot), Describe(loadedSnapshot));
        Assert.Equal(Describe(collection), Describe(Assert.Single(data.Collections)));
        Assert.Equal(Describe(board), Describe(Assert.Single(data.Boards)));
        Assert.Equal(asset, data.Assets[asset.Hash]);
    }

    [Fact]
    public async Task Second_writer_is_rejected()
    {
        await using var first = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);

        await Assert.ThrowsAsync<ProjectStoreException>(() => SqliteProjectStore.OpenAsync(ProjectDir, Ct));
    }

    [Fact]
    public async Task Newer_schema_is_rejected_without_modifying_the_database()
    {
        await (await SqliteProjectStore.CreateAsync(ProjectDir, Ct)).DisposeAsync();
        var database = Path.Combine(ProjectDir, SqliteProjectStore.DatabaseFileName);
        using (var raw = SqliteProjectStore.Connect(ProjectDir, SqliteOpenMode.ReadWrite))
        {
            SqliteProjectStore.Execute(raw, null, $"PRAGMA journal_mode = DELETE; PRAGMA user_version = {ProjectSchema.Version + 1};");
        }

        SqliteConnection.ClearAllPools();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(database, Ct));

        var error = await Assert.ThrowsAsync<ProjectStoreException>(() => SqliteProjectStore.OpenAsync(ProjectDir, Ct));

        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database, Ct)));
    }

    [Fact]
    public async Task Collection_deletion_is_blocked_while_a_board_references_it()
    {
        var (snapshot, collection, board, asset) = SampleProject();
        await using var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);
        await store.CommitAsync(new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset], PutCollections = [collection], PutBoards = [board] }, Ct);

        await Assert.ThrowsAsync<ProjectStoreException>(() => store.CommitAsync(new ProjectChange { DeleteCollections = [collection.Id] }, Ct));
        Assert.Single((await store.LoadAsync(Ct)).Collections);

        await store.CommitAsync(new ProjectChange { DeleteBoards = [board.Id], DeleteCollections = [collection.Id] }, Ct);
        var data = await store.LoadAsync(Ct);
        Assert.Empty(data.Collections);
        Assert.Empty(data.Boards);
    }

    [Fact]
    public async Task Failed_commit_leaves_no_partial_change()
    {
        var (snapshot, collection, board, asset) = SampleProject();
        await using var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);
        var orphanBoard = BoardOperations.Create(BoardId.New(), "Orphan", collection with { }, e => new ResolvedTitle("t", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());
        var otherCollection = CollectionOperations.Create(CollectionId.New(), "Never saved", snapshot);
        var badBoard = BoardOperations.Create(BoardId.New(), "Bad", otherCollection, e => new ResolvedTitle("t", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());

        // The bad board references a collection that is not part of the project, so the whole change must roll back.
        await Assert.ThrowsAsync<ProjectStoreException>(() => store.CommitAsync(
            new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset], PutCollections = [collection], PutBoards = [orphanBoard, badBoard] }, Ct));

        var data = await store.LoadAsync(Ct);
        Assert.Empty(data.Snapshots);
        Assert.Empty(data.Collections);
        Assert.Empty(data.Boards);
        Assert.Empty(data.Assets);
    }

    [Fact]
    public async Task Entry_referencing_an_unsaved_snapshot_is_rejected()
    {
        var (snapshot, collection, _, asset) = SampleProject();
        await using var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);
        await store.CommitAsync(new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset] }, Ct);
        var unsaved = new SourceSnapshot(SnapshotId.New(), snapshot.Context, snapshot.Captures);
        var pointingAtUnsaved = CollectionOperations.Create(CollectionId.New(), "c", unsaved);

        await Assert.ThrowsAsync<ProjectStoreException>(() => store.CommitAsync(new ProjectChange { PutCollections = [pointingAtUnsaved] }, Ct));
        await store.CommitAsync(new ProjectChange { PutCollections = [collection] }, Ct);
    }

    [Fact]
    public async Task Collection_context_cannot_change()
    {
        var (snapshot, collection, _, asset) = SampleProject();
        await using var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);
        await store.CommitAsync(new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset], PutCollections = [collection] }, Ct);
        var fall = new SourceSnapshot(SnapshotId.New(), new AcquisitionContext(2025, Season.Fall, CatalogueCategory.Tv), [Capture(new SeasonKey(2025, Season.Fall), A)]);
        var moved = new CollectionState(collection.Id, collection.Name, fall.Context, fall.Id, collection.Language, CollectionOperations.Create(collection.Id, "x", fall).Entries);

        await Assert.ThrowsAsync<ProjectStoreException>(() => store.CommitAsync(new ProjectChange { AddSnapshots = [fall], PutCollections = [moved] }, Ct));
    }

    [Fact]
    public async Task Asset_with_conflicting_properties_is_rejected()
    {
        var (_, _, _, asset) = SampleProject();
        await using var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);
        await store.CommitAsync(new ProjectChange { AddAssets = [asset] }, Ct);

        await store.CommitAsync(new ProjectChange { AddAssets = [asset] }, Ct);
        await Assert.ThrowsAsync<ProjectStoreException>(() => store.CommitAsync(
            new ProjectChange { AddAssets = [new AssetRecord(asset.Hash, AssetFormat.Png, asset.Width, asset.Height, asset.ByteLength)] }, Ct));
    }

    [Fact]
    public async Task Database_constraints_reject_duplicate_placements_written_directly()
    {
        var (snapshot, collection, board, asset) = SampleProject();
        await using (var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct))
        {
            await store.CommitAsync(new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset], PutCollections = [collection], PutBoards = [board] }, Ct);
        }

        using var raw = SqliteProjectStore.Connect(ProjectDir, SqliteOpenMode.ReadWrite);
        var tier = board.Tiers[0].Id.ToString();
        var ex = Assert.Throws<SqliteException>(() => SqliteProjectStore.Execute(raw, null,
            "UPDATE board_entries SET tier_id = $tier, tier_position = 0 WHERE livechart_id = $item", ("$tier", tier), ("$item", C.Value)));
        Assert.Equal(19, ex.SqliteErrorCode);
    }

    [Fact]
    public async Task Repeated_edits_replace_state_with_exact_order()
    {
        var (snapshot, collection, board, asset) = SampleProject();
        await using var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct);
        await store.CommitAsync(new ProjectChange { AddSnapshots = [snapshot], AddAssets = [asset], PutCollections = [collection], PutBoards = [board] }, Ct);

        var reordered = CollectionOperations.Move(collection, C, 0);
        var moved = BoardOperations.MoveToTier(BoardOperations.ReturnToUnranked(board, A), B, board.Tiers[1].Id, 0);
        await store.CommitAsync(new ProjectChange { PutCollections = [reordered], PutBoards = [moved] }, Ct);

        var data = await store.LoadAsync(Ct);
        Assert.Equal(Describe(reordered), Describe(data.Collections[0]));
        Assert.Equal(Describe(moved), Describe(data.Boards[0]));
    }

    [Fact]
    public async Task Sidebar_order_duplicate_board_entries_and_source_details_survive_reopen()
    {
        var details = new SourceDetails(
            "8.49 out of 10",
            "2025-07-03",
            "TV · 12 eps",
            "A synopsis.",
            ["Science SARU"],
            ["Action", "Comedy"],
            new Uri("https://anilist.co/anime/185660"),
            new Uri("https://myanimelist.net/anime/60543"));
        var extras = new AnimeExtraDetails(
            A,
            new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.Zero),
            details,
            [
                new AnimeVideo("OP", new Uri("https://www.youtube.com/watch?v=DCCRNzKvWRg"), AnimeVideoKind.Opening, "1:44", "JA", null, false),
                new AnimeVideo("ED", new Uri("https://www.youtube.com/watch?v=AhNYcpN1pEU"), AnimeVideoKind.Ending, "1:42", "JA", null, false),
                new AnimeVideo("Climax PV", new Uri("https://www.youtube.com/watch?v=BZWWBM9bi2g"), AnimeVideoKind.Other, "1:46", "JA", "EN", true),
            ]);
        var capture = new SeasonCapture(
            new SeasonKey(2025, Season.Summer),
            CatalogueCategory.Tv,
            new Uri("https://www.livechart.me/summer-2025/tv"),
            new DateTimeOffset(2026, 11, 17, 22, 0, 0, TimeSpan.Zero),
            "test",
            [
                new SourceOccurrence(0, A, new SourceTitles("Title A", "English A", "Romaji A", "Native A"), [], details),
                new SourceOccurrence(1, B, new SourceTitles("Title B", null, "Romaji B", null), []),
            ]);
        var snapshot = new SourceSnapshot(SnapshotId.New(), new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv), [capture]);
        var c1 = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "First Collection", snapshot), true);
        var c2 = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Second Collection", snapshot), true);
        var b1 = BoardOperations.Create(BoardId.New(), "Alpha Board", c1, e => new ResolvedTitle($"T {e.Item}", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());
        b1 = BoardOperations.DuplicateEntry(b1, A);
        b1 = BoardOperations.MoveToTier(b1, new BoardEntryKey(A, 2), b1.Tiers[0].Id, 0);
        var b2 = BoardOperations.Create(BoardId.New(), "Beta Board", c1, e => new ResolvedTitle($"T {e.Item}", TitleSource.SourceDefault, false), _ => true, BoardOperations.DefaultTiers());

        await using (var store = await SqliteProjectStore.CreateAsync(ProjectDir, Ct))
        {
            await store.CommitAsync(new ProjectChange
            {
                AddSnapshots = [snapshot],
                PutCollections = [c1, c2],
                PutBoards = [b1, b2],
                CollectionOrder = [c2.Id, c1.Id],
                BoardOrder = [b2.Id, b1.Id],
                PutItemExtras = [extras],
            }, Ct);
        }

        await using var reopened = await SqliteProjectStore.OpenAsync(ProjectDir, Ct);
        var data = await reopened.LoadAsync(Ct);
        Assert.Equal([c2.Id, c1.Id], data.Collections.Select(c => c.Id));
        Assert.Equal([b2.Id, b1.Id], data.Boards.Select(b => b.Id));
        Assert.Equal(details, data.Snapshots[snapshot.Id].Captures[0].Occurrences[0].Details);
        Assert.Equal(extras, data.ItemExtras[A]);
        var loadedB1 = data.Boards.Single(b => b.Id == b1.Id);
        Assert.Equal([new BoardEntryKey(A, 1), new BoardEntryKey(A, 2), new BoardEntryKey(B, 1)], loadedB1.Entries.Select(e => e.Key));
        Assert.Equal([new BoardEntryKey(A, 2)], loadedB1.Placements[loadedB1.Tiers[0].Id]);
    }

    [Fact]
    public async Task Version_2_database_upgrades_safely_to_version_3_and_rolls_back_on_interrupted_upgrade()
    {
        Directory.CreateDirectory(Path.Combine(ProjectDir, "assets"));
        Directory.CreateDirectory(Path.Combine(ProjectDir, "cache"));
        var database = Path.Combine(ProjectDir, SqliteProjectStore.DatabaseFileName);
        var snapshotId = SnapshotId.New();
        var colId = CollectionId.New();
        var boardId = BoardId.New();
        var tierId = TierId.New();

        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true }.ToString()))
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
            SqliteProjectStore.Execute(raw, null, "INSERT INTO occurrences VALUES ($s, 2025, 2, 0, 101, 'V2 Show', NULL, 'V2 Show', NULL, '8.5', '2025-07-03', 'TV · 12 eps', 'V2 synopsis.', '[\"Science SARU\"]', '[\"Action\"]')", ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'V2 Col', 2025, 2, 0, $s, 0, 0)", ("$c", colId.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collection_entries VALUES ($c, 101, 0, 1, 0, NULL, $s, 2025, 2, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)", ("$c", colId.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO boards VALUES ($b, $c, 'V2 Board', 0)", ("$b", boardId.ToString()), ("$c", colId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO tiers VALUES ($b, $t, 0, 'S', '#FF7F7F')", ("$b", boardId.ToString()), ("$t", tierId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO board_entries VALUES ($b, 101, 1, 0, 'V2 Show', NULL, $t, 0)", ("$b", boardId.ToString()), ("$t", tierId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO board_entries VALUES ($b, 101, 2, 1, 'V2 Show', NULL, NULL, NULL)", ("$b", boardId.ToString()));
        }

        SqliteConnection.ClearAllPools();
        var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(database, Ct));

        await Assert.ThrowsAsync<ProjectStoreException>(() => SqliteProjectStore.OpenAsync(ProjectDir, () => throw new IOException("Simulated crash"), Ct));
        Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(database, Ct)));

        await using var upgraded = await SqliteProjectStore.OpenAsync(ProjectDir, Ct);
        var loaded = await upgraded.LoadAsync(Ct);
        var occ = loaded.Snapshots[snapshotId].Captures[0].Occurrences[0];
        Assert.Equal("V2 synopsis.", occ.Details.Synopsis);
        Assert.Equal(["Science SARU"], occ.Details.Studios);
        Assert.Equal(2, loaded.Boards.Single().Entries.Length);

        var archive = Path.Combine(_root, "v2-upgraded.animerank");
        await Infrastructure.Archives.ProjectArchiveExporter.ExportAsync(upgraded, archive, replaceExisting: false, Infrastructure.Archives.ArchiveLimits.Default, Ct);
        await Infrastructure.Archives.ProjectArchiveImporter.ValidateAsync(archive, Infrastructure.Archives.ArchiveLimits.Default, Ct);
    }

    [Fact]
    public async Task Version_1_database_upgrades_safely_to_version_2_and_rolls_back_on_interrupted_upgrade()
    {
        Directory.CreateDirectory(Path.Combine(ProjectDir, "assets"));
        Directory.CreateDirectory(Path.Combine(ProjectDir, "cache"));
        var database = Path.Combine(ProjectDir, SqliteProjectStore.DatabaseFileName);
        var snapshotId = SnapshotId.New();
        var colZeta = CollectionId.New();
        var colAlphaA = new CollectionId(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var colAlphaB = new CollectionId(Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var boardZeta = BoardId.New();
        var boardAlpha = BoardId.New();
        var tierId = TierId.New();

        // Create a real schema-v1 SQLite database on disk (inserted out of V1 alphabetical display order).
        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, ForeignKeys = true }.ToString()))
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
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'Zeta', 2025, 2, 0, $s, 0)", ("$c", colZeta.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'alpha', 2025, 2, 0, $s, 0)", ("$c", colAlphaB.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collections VALUES ($c, 'Alpha', 2025, 2, 0, $s, 0)", ("$c", colAlphaA.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO collection_entries VALUES ($c, 101, 0, 1, 0, NULL, $s, 2025, 2, 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL)", ("$c", colZeta.ToString()), ("$s", snapshotId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO boards VALUES ($b, $c, 'Zeta Board')", ("$b", boardZeta.ToString()), ("$c", colZeta.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO boards VALUES ($b, $c, 'Alpha Board')", ("$b", boardAlpha.ToString()), ("$c", colZeta.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO tiers VALUES ($b, $t, 0, 'S', '#FF7F7F')", ("$b", boardZeta.ToString()), ("$t", tierId.ToString()));
            SqliteProjectStore.Execute(raw, null, "INSERT INTO board_entries VALUES ($b, 101, 0, 'Legacy Show', NULL, $t, 0)", ("$b", boardZeta.ToString()), ("$t", tierId.ToString()));
        }

        SqliteConnection.ClearAllPools();
        var beforeHash = SHA256.HashData(await File.ReadAllBytesAsync(database, Ct));

        // 1. An interrupted upgrade before final swap must preserve the v1 database intact and leave no staging files.
        await Assert.ThrowsAsync<ProjectStoreException>(() => SqliteProjectStore.OpenAsync(ProjectDir, () => throw new IOException("Simulated crash before swap"), Ct));
        Assert.Equal(beforeHash, SHA256.HashData(await File.ReadAllBytesAsync(database, Ct)));
        Assert.Equal([database], Directory.GetFiles(ProjectDir));

        // 2. Opening normally completes the upgrade, preserves V1 alphabetical order (+ ID tie-breaker) and placements, and exports cleanly!
        await using var upgraded = await SqliteProjectStore.OpenAsync(ProjectDir, Ct);
        var loaded = await upgraded.LoadAsync(Ct);
        Assert.Equal([colAlphaA, colAlphaB, colZeta], loaded.Collections.Select(c => c.Id));
        Assert.Equal([boardAlpha, boardZeta], loaded.Boards.Select(b => b.Id));
        var loadedBoard = loaded.Boards.Single(b => b.Id == boardZeta);
        Assert.Equal([new BoardEntryKey(A, 1)], loadedBoard.Placements[tierId]);

        var archive = Path.Combine(_root, "upgraded.animerank");
        await Infrastructure.Archives.ProjectArchiveExporter.ExportAsync(upgraded, archive, replaceExisting: false, Infrastructure.Archives.ArchiveLimits.Default, Ct);
        await Infrastructure.Archives.ProjectArchiveImporter.ValidateAsync(archive, Infrastructure.Archives.ArchiveLimits.Default, Ct);
    }

    private static SeasonCapture Capture(SeasonKey season, params LiveChartId[] ids) =>
        new(season, CatalogueCategory.Tv, new Uri($"https://www.livechart.me/{season.Season.ToString().ToLowerInvariant()}-{season.Year}/tv"),
            new DateTimeOffset(2026, 11, 17, 22, 0, 0, TimeSpan.Zero), "test",
            ids.Select((id, i) => new SourceOccurrence(i, id, new SourceTitles($"Title {id}", i == 0 ? $"English {id}" : null, $"Romaji {id}", null),
                [new ImageCandidate("large", new Uri($"https://u.livechart.me/anime/{id}/poster_image/a.webp/large.jpg")), new ImageCandidate("small", new Uri($"https://u.livechart.me/anime/{id}/poster_image/a.webp/small.jpg"))])).ToImmutableArray());

    private static (SourceSnapshot, CollectionState, BoardState, AssetRecord) SampleProject()
    {
        var snapshot = new SourceSnapshot(SnapshotId.New(), new AcquisitionContext(2025, Season.Summer, CatalogueCategory.Tv), [Capture(new SeasonKey(2025, Season.Summer), A, B, C, A)]);
        var asset = new AssetRecord(new AssetHash(new string('a', 64)), AssetFormat.Jpeg, 350, 500, 45_754);
        var collection = CollectionOperations.Create(CollectionId.New(), "Summer 2025", snapshot);
        collection = CollectionOperations.SetAllIncluded(collection, true);
        collection = CollectionOperations.SetTitleOverride(collection, B, "Override");
        collection = CollectionOperations.SetLanguage(collection, TitleLanguage.Romanized);
        var occurrence = collection.Entry(A).AcceptedOccurrence;
        Assert.True(CollectionOperations.TryAttachDownloadedCover(collection, A,
            new CoverProvenance(occurrence, new Uri("https://u.livechart.me/anime/101/poster_image/a.webp/large.jpg"), new Uri("https://u.livechart.me/anime/101/poster_image/a.webp/large.jpg"), "Accept: image/jpeg,image/png", asset.Hash), out collection));
        var board = BoardOperations.Create(BoardId.New(), "Board", collection, e => TitleResolver.Resolve(snapshot.Occurrence(e.AcceptedOccurrence).Titles, collection.Language, e.TitleOverride), _ => true, BoardOperations.DefaultTiers());
        board = BoardOperations.MoveToTier(board, A, board.Tiers[0].Id, 0);
        board = BoardOperations.MoveToTier(board, B, board.Tiers[0].Id, 0);
        return (snapshot, collection, board, asset);
    }

    private static string Describe(SourceSnapshot s) => string.Join("\n",
        s.Captures.Select(c => $"{c.Season}|{c.Category}|{c.RequestUrl}|{c.RetrievedAtUtc:O}|{c.ParserVersion}|" + string.Join(";",
            c.Occurrences.Select(o => $"{o.Ordinal}:{o.Item}:{o.Titles}:{string.Join(",", o.ImageCandidates)}"))));

    private static string Describe(CollectionState c) =>
        $"{c.Id}|{c.Name}|{c.Context}|{c.AppliedSnapshot}|{c.Language}|" + string.Join(";", c.Entries.Select(e => e.ToString()));

    private static string Describe(BoardState b) =>
        $"{b.Id}|{b.Collection}|{b.Name}|" + string.Join(";", b.Tiers) + "|" + string.Join(";", b.Entries) + "|" +
        string.Join(";", b.Tiers.Select(t => t.Id + "=" + string.Join(",", b.Placements[t.Id])));
}
