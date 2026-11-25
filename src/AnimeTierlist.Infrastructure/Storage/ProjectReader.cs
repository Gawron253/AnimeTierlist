using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Storage;

/// <summary>Reads a project database into domain objects. Domain constructors re-validate every invariant.</summary>
internal static class ProjectReader
{
    public static ProjectData Read(SqliteConnection connection)
    {
        var snapshots = ReadSnapshots(connection);
        var assets = ReadAssets(connection);
        var collections = ReadCollections(connection);
        var boards = ReadBoards(connection);
        var itemExtras = ReadItemExtras(connection);
        return new ProjectData(snapshots, collections, boards, assets, itemExtras);
    }

    private static ImmutableDictionary<SnapshotId, SourceSnapshot> ReadSnapshots(SqliteConnection c)
    {
        var candidates = new Dictionary<(string, int, int, int), List<ImageCandidate>>();
        foreach (var r in Rows(c, "SELECT snapshot_id, year, season, ordinal, variant, url FROM image_candidates ORDER BY snapshot_id, year, season, ordinal, rank"))
        {
            var key = (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
            if (!candidates.TryGetValue(key, out var list))
            {
                candidates[key] = list = [];
            }

            list.Add(new ImageCandidate(r.GetString(4), new Uri(r.GetString(5))));
        }

        var occurrences = new Dictionary<(string, int, int), ImmutableArray<SourceOccurrence>.Builder>();
        foreach (var r in Rows(c, """
            SELECT snapshot_id, year, season, ordinal, livechart_id,
                   displayed_title, english_title, romanized_title, native_title,
                   rating, premiere, metadata, synopsis, studios_json, tags_json, anilist_url, myanimelist_url
            FROM occurrences ORDER BY snapshot_id, year, season, ordinal
            """))
        {
            var key = (r.GetString(0), r.GetInt32(1), r.GetInt32(2));
            if (!occurrences.TryGetValue(key, out var builder))
            {
                occurrences[key] = builder = ImmutableArray.CreateBuilder<SourceOccurrence>();
            }

            var ordinal = r.GetInt32(3);
            var titles = new SourceTitles(r.GetString(5), NullableString(r, 6), NullableString(r, 7), NullableString(r, 8));
            var images = candidates.TryGetValue((key.Item1, key.Item2, key.Item3, ordinal), out var found) ? found.ToImmutableArray() : [];
            var details = new SourceDetails(
                NullableString(r, 9),
                NullableString(r, 10),
                NullableString(r, 11),
                NullableString(r, 12),
                ReadStringArray(NullableString(r, 13)),
                ReadStringArray(NullableString(r, 14)),
                NullableUri(r, 15),
                NullableUri(r, 16));
            builder.Add(new SourceOccurrence(ordinal, new LiveChartId(r.GetInt32(4)), titles, images, details));
        }

        var captures = new Dictionary<string, List<SeasonCapture>>();
        foreach (var r in Rows(c, "SELECT snapshot_id, year, season, category, request_url, retrieved_at, parser_version FROM season_captures"))
        {
            var key = (r.GetString(0), r.GetInt32(1), r.GetInt32(2));
            var items = occurrences.TryGetValue(key, out var builder) ? builder.ToImmutable() : [];
            var capture = new SeasonCapture(new SeasonKey(key.Item2, (Season)key.Item3), (CatalogueCategory)r.GetInt32(3), new Uri(r.GetString(4)),
                DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetString(6), items);
            if (!captures.TryGetValue(key.Item1, out var list))
            {
                captures[key.Item1] = list = [];
            }

            list.Add(capture);
        }

        var result = ImmutableDictionary.CreateBuilder<SnapshotId, SourceSnapshot>();
        foreach (var r in Rows(c, "SELECT id, year, season, category FROM snapshots"))
        {
            var id = new SnapshotId(Guid.Parse(r.GetString(0)));
            var context = new AcquisitionContext(r.GetInt32(1), r.IsDBNull(2) ? null : (Season)r.GetInt32(2), (CatalogueCategory)r.GetInt32(3));
            result.Add(id, new SourceSnapshot(id, context, captures.TryGetValue(r.GetString(0), out var list) ? list : []));
        }

        return result.ToImmutable();
    }

    private static ImmutableDictionary<AssetHash, AssetRecord> ReadAssets(SqliteConnection c)
    {
        var result = ImmutableDictionary.CreateBuilder<AssetHash, AssetRecord>();
        foreach (var r in Rows(c, "SELECT hash, format, width, height, byte_length FROM assets"))
        {
            var format = r.GetString(1) switch
            {
                "jpg" => AssetFormat.Jpeg,
                "png" => AssetFormat.Png,
                "webp" => AssetFormat.WebP,
                var other => throw new ProjectStoreException($"Unknown asset format '{other}'."),
            };
            var hash = new AssetHash(r.GetString(0));
            result.Add(hash, new AssetRecord(hash, format, r.GetInt32(2), r.GetInt32(3), r.GetInt64(4)));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<CollectionState> ReadCollections(SqliteConnection c)
    {
        var entries = new Dictionary<string, ImmutableArray<CollectionEntry>.Builder>();
        foreach (var r in Rows(c, """
            SELECT collection_id, livechart_id, included, source_missing, title_override,
                   accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal,
                   cover_asset_hash, cover_snapshot_id, cover_year, cover_season, cover_ordinal, cover_requested_url, cover_final_url, cover_request_context
            FROM collection_entries ORDER BY collection_id, ordinal
            """))
        {
            var accepted = new OccurrenceRef(new SnapshotId(Guid.Parse(r.GetString(5))), new SeasonKey(r.GetInt32(6), (Season)r.GetInt32(7)), r.GetInt32(8));
            CoverProvenance? cover = r.IsDBNull(9)
                ? null
                : new CoverProvenance(
                    new OccurrenceRef(new SnapshotId(Guid.Parse(r.GetString(10))), new SeasonKey(r.GetInt32(11), (Season)r.GetInt32(12)), r.GetInt32(13)),
                    new Uri(r.GetString(14)), new Uri(r.GetString(15)), r.GetString(16), new AssetHash(r.GetString(9)));
            var entry = new CollectionEntry(new LiveChartId(r.GetInt32(1)), r.GetInt32(2) == 1, accepted, r.GetInt32(3) == 1, cover, NullableString(r, 4));
            var key = r.GetString(0);
            if (!entries.TryGetValue(key, out var builder))
            {
                entries[key] = builder = ImmutableArray.CreateBuilder<CollectionEntry>();
            }

            builder.Add(entry);
        }

        var result = ImmutableArray.CreateBuilder<CollectionState>();
        var expectedOrdinal = 0;
        foreach (var r in Rows(c, "SELECT id, name, year, season, category, applied_snapshot_id, title_language, ordinal FROM collections ORDER BY ordinal"))
        {
            var ordinal = r.GetInt32(7);
            if (ordinal != expectedOrdinal++)
            {
                throw new ProjectStoreException($"Collection ordinals must be contiguous from zero; found {ordinal}.");
            }

            var context = new AcquisitionContext(r.GetInt32(2), r.IsDBNull(3) ? null : (Season)r.GetInt32(3), (CatalogueCategory)r.GetInt32(4));
            result.Add(new CollectionState(new CollectionId(Guid.Parse(r.GetString(0))), r.GetString(1), context, new SnapshotId(Guid.Parse(r.GetString(5))),
                (TitleLanguage)r.GetInt32(6), entries.TryGetValue(r.GetString(0), out var builder) ? builder.ToImmutable() : []));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<BoardState> ReadBoards(SqliteConnection c)
    {
        var tiers = new Dictionary<string, ImmutableArray<Tier>.Builder>();
        foreach (var r in Rows(c, "SELECT board_id, id, name, color FROM tiers ORDER BY board_id, ordinal"))
        {
            if (!tiers.TryGetValue(r.GetString(0), out var builder))
            {
                tiers[r.GetString(0)] = builder = ImmutableArray.CreateBuilder<Tier>();
            }

            builder.Add(new Tier(new TierId(Guid.Parse(r.GetString(1))), r.GetString(2), ParseColor(r.GetString(3))));
        }

        var entries = new Dictionary<string, ImmutableArray<BoardEntry>.Builder>();
        var placements = new Dictionary<string, Dictionary<TierId, SortedList<int, BoardEntryKey>>>();
        foreach (var r in Rows(c, "SELECT board_id, livechart_id, copy_number, pinned_title, pinned_asset_hash, tier_id, tier_position FROM board_entries ORDER BY board_id, seed"))
        {
            var board = r.GetString(0);
            var item = new LiveChartId(r.GetInt32(1));
            var copyNumber = r.GetInt32(2);
            var entryKey = new BoardEntryKey(item, copyNumber);
            if (!entries.TryGetValue(board, out var builder))
            {
                entries[board] = builder = ImmutableArray.CreateBuilder<BoardEntry>();
                placements[board] = [];
            }

            builder.Add(new BoardEntry(item, r.GetString(3), r.IsDBNull(4) ? null : new AssetHash(r.GetString(4)), copyNumber));
            if (!r.IsDBNull(5))
            {
                var tier = new TierId(Guid.Parse(r.GetString(5)));
                if (!placements[board].TryGetValue(tier, out var ordered))
                {
                    placements[board][tier] = ordered = [];
                }

                ordered.Add(r.GetInt32(6), entryKey);
            }
        }

        var result = ImmutableArray.CreateBuilder<BoardState>();
        var expectedOrdinal = 0;
        foreach (var r in Rows(c, "SELECT id, collection_id, name, ordinal FROM boards ORDER BY ordinal"))
        {
            var ordinal = r.GetInt32(3);
            if (ordinal != expectedOrdinal++)
            {
                throw new ProjectStoreException($"Board ordinals must be contiguous from zero; found {ordinal}.");
            }

            var id = r.GetString(0);
            var boardPlacements = placements.TryGetValue(id, out var found)
                ? found.ToImmutableDictionary(p => p.Key, p => p.Value.Values.ToImmutableArray())
                : ImmutableDictionary<TierId, ImmutableArray<BoardEntryKey>>.Empty;
            result.Add(new BoardState(new BoardId(Guid.Parse(id)), new CollectionId(Guid.Parse(r.GetString(1))), r.GetString(2),
                tiers.TryGetValue(id, out var tierList) ? tierList.ToImmutable() : [],
                entries.TryGetValue(id, out var entryList) ? entryList.ToImmutable() : [],
                boardPlacements));
        }

        return result.ToImmutable();
    }

    private static ImmutableDictionary<LiveChartId, AnimeExtraDetails> ReadItemExtras(SqliteConnection c)
    {
        var videosByItem = new Dictionary<int, ImmutableArray<AnimeVideo>.Builder>();
        var expectedVideoOrdinal = new Dictionary<int, int>();
        foreach (var r in Rows(c, """
            SELECT livechart_id, ordinal, title, url, kind, duration, audio_language, subtitles, has_spoilers
            FROM item_videos ORDER BY livechart_id, ordinal
            """))
        {
            var id = r.GetInt32(0);
            var ordinal = r.GetInt32(1);
            var expected = expectedVideoOrdinal.GetValueOrDefault(id);
            if (ordinal != expected)
            {
                throw new ProjectStoreException($"Video ordinals for item {id} must be contiguous from zero; found {ordinal}.");
            }

            expectedVideoOrdinal[id] = expected + 1;
            if (!videosByItem.TryGetValue(id, out var builder))
            {
                videosByItem[id] = builder = ImmutableArray.CreateBuilder<AnimeVideo>();
            }

            builder.Add(new AnimeVideo(
                r.GetString(2),
                new Uri(r.GetString(3)),
                (AnimeVideoKind)r.GetInt32(4),
                NullableString(r, 5),
                NullableString(r, 6),
                NullableString(r, 7),
                r.GetInt32(8) == 1));
        }

        var result = ImmutableDictionary.CreateBuilder<LiveChartId, AnimeExtraDetails>();
        foreach (var r in Rows(c, """
            SELECT livechart_id, retrieved_at, videos_fetched, profile_fetched, rating, premiere, metadata, synopsis, studios_json, tags_json, anilist_url, myanimelist_url
            FROM item_extras
            """))
        {
            var id = r.GetInt32(0);
            var item = new LiveChartId(id);
            var retrievedAt = DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var videosFetched = r.GetInt32(2) == 1;
            var profileFetched = r.GetInt32(3) == 1;
            var details = new SourceDetails(
                NullableString(r, 4),
                NullableString(r, 5),
                NullableString(r, 6),
                NullableString(r, 7),
                ReadStringArray(NullableString(r, 8)),
                ReadStringArray(NullableString(r, 9)),
                NullableUri(r, 10),
                NullableUri(r, 11));
            var videos = videosByItem.TryGetValue(id, out var builder) ? builder.ToImmutable() : [];
            result.Add(item, new AnimeExtraDetails(item, retrievedAt, details, videos, videosFetched, profileFetched));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<string> ReadStringArray(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<ImmutableArray<string>>(json);

    private static Uri? NullableUri(SqliteDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : new Uri(r.GetString(ordinal));

    private static TierColor ParseColor(string hex) => new(
        byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string? NullableString(SqliteDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static IEnumerable<SqliteDataReader> Rows(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return reader;
        }
    }
}
