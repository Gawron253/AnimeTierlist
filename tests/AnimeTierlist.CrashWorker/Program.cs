using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AnimeTierlist.Application.Assets;
using AnimeTierlist.Application.Session;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using AnimeTierlist.Infrastructure.Archives;
using AnimeTierlist.Infrastructure.Assets;
using AnimeTierlist.Infrastructure.Exports;
using AnimeTierlist.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace AnimeTierlist.CrashWorker;

/// <summary>
/// Runs one durable operation so a test can terminate this process at an arbitrary point and inspect the result.
/// Progress lines go to standard output, which is flushed per line. Exit code 0 means the operation finished.
/// </summary>
public static class Program
{
    private static readonly AcquisitionContext Context = new(2025, Season.Summer, CatalogueCategory.Tv);

    /// <summary>Extra image candidates per occurrence in an edit run's refresh snapshots.</summary>
    private const int RefreshCandidates = 40;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["seed", var project, var count]:
                    await SeedAsync(project, int.Parse(count, CultureInfo.InvariantCulture));
                    break;
                case ["edit", var project, var steps]:
                    await EditAsync(project, int.Parse(steps, CultureInfo.InvariantCulture));
                    break;
                case ["verify", var project]:
                    Console.WriteLine("digest " + await VerifyAsync(project));
                    break;
                case ["export-png", var project, var destination, var replace]:
                    await WithProjectAsync(project, (state, assets, _) =>
                        BoardPngExporter.ExportAsync(state.Boards.Values.Single(), state.Assets, assets, destination, includeUnranked: true, replace == "1", CancellationToken.None));
                    break;
                case ["export-covers", var project, var destination]:
                    await WithProjectAsync(project, (state, assets, _) =>
                    {
                        var collection = state.Collections.Values.Single();
                        return SelectedCoverExporter.ExportAsync(collection, e => state.ResolveTitle(collection, e).Text, state.Assets, assets, destination, CancellationToken.None);
                    });
                    break;
                case ["export-archive", var project, var destination, var replace]:
                    await WithProjectAsync(project, (_, _, store) => ProjectArchiveExporter.ExportAsync(store, destination, replace == "1", ArchiveLimits.Default, CancellationToken.None));
                    break;
                case ["import", var archive, var destination]:
                    await ProjectArchiveImporter.ImportAsync(archive, destination, ArchiveLimits.Default, CancellationToken.None);
                    break;
                case ["repair", var project, var source]:
                    await RepairAsync(project, source);
                    break;
                default:
                    Console.Error.WriteLine("Unknown command.");
                    return 64;
            }

            // Lets a test kill the process after the operation finished but before it exits.
            if (int.TryParse(Environment.GetEnvironmentVariable("ANIMETIERLIST_WORKER_LINGER_MS"), CultureInfo.InvariantCulture, out var linger))
            {
                await Task.Delay(linger);
            }

            Console.WriteLine("done");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>A project with one collection (all included, each with a cover) and one board with some ranked shows.</summary>
    private static async Task SeedAsync(string project, int count)
    {
        var snapshot = Snapshot(Enumerable.Range(1, count).Select(i => new LiveChartId(700_000 + i)).ToList());
        var assets = new AssetStore(project);
        var collection = CollectionOperations.SetAllIncluded(CollectionOperations.Create(CollectionId.New(), "Crash test", snapshot), true);
        var records = new List<AssetRecord>();
        foreach (var entry in collection.Entries.ToList())
        {
            var (bytes, record) = Cover(entry.Item.Value);
            records.Add(await assets.InstallAsync(bytes, record, CancellationToken.None));
            if (!CollectionOperations.TryAttachDownloadedCover(collection, entry.Item, Provenance(snapshot, entry.AcceptedOccurrence, record.Hash), out collection))
            {
                throw new InvalidOperationException("The seed cover did not attach.");
            }
        }

        await using var session = await ProjectSession.OpenAsync(await SqliteProjectStore.CreateAsync(project, CancellationToken.None), CancellationToken.None);
        Require(session.Execute("Seed", s =>
        {
            var state = records.Aggregate(s.WithSnapshot(snapshot), (acc, a) => acc.WithAsset(a)).WithCollection(collection);
            var board = BoardOperations.Create(BoardId.New(), "Crash board", collection, e => state.ResolveTitle(collection, e), _ => true, BoardOperations.DefaultTiers());
            foreach (var (item, n) in board.Entries.Take(count / 2).Select((e, n) => (e.Item, n)))
            {
                var tier = board.Tiers[n % board.Tiers.Length].Id;
                board = BoardOperations.MoveToTier(board, item, tier, board.Placements[tier].Length);
            }

            return state.WithBoard(board);
        }));
        await FlushAsync(session);
    }

    /// <summary>
    /// Each step installs a new cover file (asset rename) and commits it as the selected cover of one entry; every
    /// fifth step also commits a new refresh snapshot that adopts a rotated source order. "installing" is printed
    /// before the cover install, "pending" when the command produces its revision, "committing" when the store's
    /// commit starts, "writing" when a refresh commit's open SQLite transaction has written its first row (see
    /// <see cref="ReportingStore"/>), and "committed" after it is durable; "pending" and "committed" carry the digest
    /// of that revision. The covers are large noise images so their staged write takes a few
    /// milliseconds, long enough for a test to kill the process inside it.
    /// </summary>
    private static async Task EditAsync(string project, int steps)
    {
        await using var session = await ProjectSession.OpenAsync(ReportingStore.Wrap(await SqliteProjectStore.OpenAsync(project, CancellationToken.None)), CancellationToken.None);
        var assets = new AssetStore(project);
        for (var step = 1; step <= steps; step++)
        {
            var current = session.Current;
            var collection = current.Collections.Values.Single();
            var entry = collection.Entries[step % collection.Entries.Length];
            var (bytes, record) = Cover(RandomNumberGenerator.GetInt32(1, int.MaxValue), large: true);
            Console.WriteLine($"installing {step}");
            await assets.InstallAsync(bytes, record, CancellationToken.None);
            Require(session.Execute("Edit", s =>
            {
                var c = s.Collection(collection.Id);
                if (step % 5 == 0)
                {
                    var order = c.Entries.Select(e => e.Item).ToList();
                    var refresh = Snapshot([.. order.Skip(1), order[0]], extraCandidates: RefreshCandidates);
                    s = s.WithSnapshot(refresh);
                    c = CollectionOperations.ApplyRefresh(c, refresh, adoptSourceOrder: true);
                }

                var e = c.Entry(entry.Item);
                c = CollectionOperations.MarkCoverMissing(c, e.Item);
                if (!CollectionOperations.TryAttachDownloadedCover(c, e.Item, Provenance(s.Snapshots[e.AcceptedOccurrence.Snapshot], e.AcceptedOccurrence, record.Hash), out c))
                {
                    throw new InvalidOperationException("The edit cover did not attach.");
                }

                var next = s.WithAsset(record).WithCollection(c);
                Console.WriteLine($"pending {step} {Digest(next)}");
                return next;
            }));
            await FlushAsync(session);
            Console.WriteLine($"committed {step} {Digest(session.Durable)}");
        }
    }

    /// <summary>
    /// Opens the project in this fresh process (domain validation included), checks SQLite integrity and foreign keys,
    /// requires every referenced cover file to match its record, and returns the revision digest.
    /// </summary>
    private static async Task<string> VerifyAsync(string project)
    {
        string digest;
        await using (var session = await ProjectSession.OpenAsync(await SqliteProjectStore.OpenAsync(project, CancellationToken.None), CancellationToken.None))
        {
            var state = session.Current;
            var assets = new AssetStore(project);
            var referenced = state.Collections.Values.SelectMany(c => c.Entries).Select(e => e.Cover?.Asset)
                .Concat(state.Boards.Values.SelectMany(b => b.Entries).Select(e => e.PinnedCover)).OfType<AssetHash>().Distinct();
            foreach (var hash in referenced)
            {
                if (await assets.CheckAsync(state.Assets[hash], CancellationToken.None) != AssetAvailability.Available)
                {
                    throw new InvalidOperationException($"Referenced cover {hash} is not available.");
                }
            }

            digest = Digest(state);
        }

        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(project, SqliteProjectStore.DatabaseFileName),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        if (command.ExecuteScalar() as string != "ok")
        {
            throw new InvalidOperationException("integrity_check failed.");
        }

        command.CommandText = "PRAGMA foreign_key_check";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                throw new InvalidOperationException("foreign_key_check reported a violation.");
            }
        }

        return digest;
    }

    /// <summary>
    /// Repairs every damaged or missing recorded cover through the same-hash atomic install, reading the original
    /// bytes from <paramref name="source"/>/&lt;hash&gt; (standing in for a re-download). Prints "repairing" before and
    /// "repaired" after each install.
    /// </summary>
    private static async Task RepairAsync(string project, string source)
    {
        ImmutableArray<AssetRecord> records;
        await using (var store = await SqliteProjectStore.OpenAsync(project, CancellationToken.None))
        {
            records = [.. (await store.LoadAsync(CancellationToken.None)).Assets.Values.OrderBy(a => a.Hash.Value, StringComparer.Ordinal)];
        }

        var assets = new AssetStore(project);
        foreach (var record in records)
        {
            if (await assets.CheckAsync(record, CancellationToken.None) == AssetAvailability.Available)
            {
                continue;
            }

            Console.WriteLine($"repairing {record.Hash}");
            await assets.InstallAsync(await File.ReadAllBytesAsync(Path.Combine(source, record.Hash.Value)), record, CancellationToken.None);
            Console.WriteLine($"repaired {record.Hash}");
        }
    }

    private static async Task WithProjectAsync(string project, Func<ProjectState, AssetStore, SqliteProjectStore, Task> export)
    {
        await using var store = await SqliteProjectStore.OpenAsync(project, CancellationToken.None);
        var state = ProjectState.FromData(await store.LoadAsync(CancellationToken.None));
        await export(state, new AssetStore(project), store);
    }

    private static async Task FlushAsync(ProjectSession session)
    {
        await session.FlushAsync();
        if (session.SaveState != SaveState.Saved)
        {
            throw new InvalidOperationException("The session did not save: " + session.Failure);
        }
    }

    private static void Require(SessionResult result)
    {
        if (result is not SessionResult.Accepted)
        {
            throw new InvalidOperationException("The command was not accepted: " + result);
        }
    }

    /// <summary>
    /// Three named candidates in a deliberate non-alphabetical order, then <paramref name="extraCandidates"/> more. Refresh
    /// snapshots carry many so their transaction lasts long enough for a test to kill the process inside it.
    /// </summary>
    private static SourceSnapshot Snapshot(IReadOnlyList<LiveChartId> items, int extraCandidates = 0) =>
        new(SnapshotId.New(), Context,
        [
            new SeasonCapture(new SeasonKey(2025, Season.Summer), CatalogueCategory.Tv, new Uri("https://www.livechart.me/summer-2025/tv"), DateTimeOffset.UnixEpoch, "crash-worker",
                items.Select((id, i) => new SourceOccurrence(i, id, new SourceTitles($"Crash show {id.Value}", $"Crash English {id.Value}", $"Kurasshu {id.Value}", $"クラッシュ {id.Value}"),
                    [
                        new ImageCandidate("large", new Uri($"https://u.livechart.me/anime/{id.Value}/poster_image/large.jpg")),
                        new ImageCandidate("small", new Uri($"https://u.livechart.me/anime/{id.Value}/poster_image/small.jpg")),
                        new ImageCandidate("medium", new Uri($"https://u.livechart.me/anime/{id.Value}/poster_image/medium.jpg")),
                        .. Enumerable.Range(1, extraCandidates).Select(k => new ImageCandidate($"extra-{k}", new Uri($"https://u.livechart.me/anime/{id.Value}/poster_image/extra-{k}.jpg"))),
                    ])).ToImmutableArray()),
        ]);

    private static CoverProvenance Provenance(SourceSnapshot snapshot, OccurrenceRef occurrence, AssetHash asset)
    {
        var url = snapshot.Occurrence(occurrence).ImageCandidates[0].Url;
        return new CoverProvenance(occurrence, url, url, "Accept: image/webp,image/png,image/jpeg", asset);
    }

    private static (byte[] Bytes, AssetRecord Record) Cover(int seed, bool large = false)
    {
        using var bitmap = large ? new SKBitmap(1200, 1800, SKColorType.Rgba8888, SKAlphaType.Opaque) : new SKBitmap(175, 250);
        if (large)
        {
            var pixels = bitmap.GetPixelSpan();
            new Random(seed).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }
        else
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(new SKColor((byte)(seed * 37), (byte)(seed >> 8), (byte)(seed >> 16)));
            using var paint = new SKPaint { Color = new SKColor((byte)(seed * 13), (byte)(255 - (seed * 7)), 200), IsAntialias = true };
            canvas.DrawCircle(87, 125, 30 + (seed % 50), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        // Small covers rotate through every allowlisted format, so the archive the sandbox imports exercises the packaged decoder for each.
        var format = large ? SKEncodedImageFormat.Jpeg : (seed % 3) switch { 0 => SKEncodedImageFormat.Jpeg, 1 => SKEncodedImageFormat.Png, _ => SKEncodedImageFormat.Webp };
        using var data = image.Encode(format, 85);
        var bytes = data.ToArray();
        return ImageValidator.Validate(bytes, ImageLimits.Default) is ImageValidation.Accepted accepted
            ? (bytes, accepted.Asset)
            : throw new InvalidOperationException("The generated cover did not validate.");
    }

    /// <summary>SHA-256 of a canonical description of every persisted part of the revision.</summary>
    private static string Digest(ProjectState state)
    {
        static string Capture(SeasonCapture c) =>
            $"{c.Season}|{c.Category}|{c.RequestUrl}|{c.RetrievedAtUtc:O}|{c.ParserVersion}|" + string.Join(";", c.Occurrences.Select(o =>
                $"{o.Ordinal}:{o.Item.Value}:{o.Titles.Displayed}/{o.Titles.English}/{o.Titles.Romanized}/{o.Titles.Native}:" +
                string.Join(",", o.ImageCandidates.Select(i => i.Variant + "=" + i.Url))));
        var text = string.Join("\n",
            state.Snapshots.Values.Select(s => $"S {s.Id}|{s.Context}|" + string.Join("#", s.Captures.Select(Capture))).Order(StringComparer.Ordinal)
                .Concat(state.Assets.Values.Select(a => $"A {a.Hash}|{a.Format}|{a.Width}|{a.Height}|{a.ByteLength}").Order(StringComparer.Ordinal))
                .Concat(state.CollectionOrder.Select((id, idx) =>
                {
                    var c = state.Collections[id];
                    return $"C {idx}|{c.Id}|{c.Name}|{c.Context}|{c.AppliedSnapshot}|{c.Language}|" + string.Join(";", c.Entries);
                }))
                .Concat(state.BoardOrder.Select((id, idx) =>
                {
                    var b = state.Boards[id];
                    return $"B {idx}|{b.Id}|{b.Collection}|{b.Name}|" + string.Join(";", b.Tiers) + "|" + string.Join(";", b.Entries) + "|" +
                        string.Join(";", b.Tiers.Select(t => t.Id + "=" + string.Join(",", b.Placements[t.Id])));
                })));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Prints "committing &lt;snapshots added&gt;" when the session's write reaches the store. For a commit that adds a
    /// snapshot, it also prints "writing" from SQLite's update hook on the store's own connection when the open
    /// transaction writes its first row, so a test can kill the process inside that transaction. The connection is
    /// test-only reflection into the store; the hook only observes.
    /// </summary>
    private sealed class ReportingStore : IProjectStore
    {
        private readonly SqliteProjectStore _inner;
        private readonly SQLitePCL.delegate_update _hook;
        private int _armed;

        private ReportingStore(SqliteProjectStore inner)
        {
            _inner = inner;
            var connection = (SqliteConnection)typeof(SqliteProjectStore).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(inner)!;
            _hook = (_, _, _, _, _) =>
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Console.WriteLine("writing");
                }
            };
            SQLitePCL.raw.sqlite3_update_hook(connection.Handle, _hook, null);
        }

        public static ReportingStore Wrap(SqliteProjectStore inner) => new(inner);

        public Task<ProjectData> LoadAsync(CancellationToken cancellationToken) => _inner.LoadAsync(cancellationToken);

        public Task CommitAsync(ProjectChange change, CancellationToken cancellationToken)
        {
            Console.WriteLine($"committing {change.AddSnapshots.Length}");
            Volatile.Write(ref _armed, change.AddSnapshots.IsEmpty ? 0 : 1);
            return _inner.CommitAsync(change, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
