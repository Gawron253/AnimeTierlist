using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AnimeTierlist.Application.Storage;
using AnimeTierlist.Core;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Storage;

/// <summary>Applies a <see cref="ProjectChange"/> inside a caller-owned transaction with parameterized SQL.</summary>
internal static class ProjectWriter
{
    private const int RenumberOffset = 1_000_000_000;

    public static void Apply(SqliteConnection connection, SqliteTransaction transaction, ProjectChange change)
    {
        foreach (var snapshot in change.AddSnapshots)
        {
            InsertSnapshot(connection, transaction, snapshot);
        }

        foreach (var asset in change.AddAssets)
        {
            InsertAsset(connection, transaction, asset);
        }

        foreach (var board in change.DeleteBoards)
        {
            Exec(connection, transaction, "DELETE FROM boards WHERE id = $id", ("$id", Text(board.Value)));
        }

        foreach (var collection in change.DeleteCollections)
        {
            Exec(connection, transaction, "DELETE FROM collections WHERE id = $id", ("$id", Text(collection.Value)));
        }

        foreach (var collection in change.PutCollections)
        {
            PutCollection(connection, transaction, collection);
        }

        foreach (var board in change.PutBoards)
        {
            PutBoard(connection, transaction, board);
        }

        foreach (var extras in change.PutItemExtras)
        {
            PutItemExtras(connection, transaction, extras);
        }

        if (change.CollectionOrder is { } collectionOrder)
        {
            ApplyOrder(connection, transaction, "collections", collectionOrder.Select(id => id.ToString()).ToList());
        }
        else if (!change.DeleteCollections.IsEmpty)
        {
            CompactOrder(connection, transaction, "collections");
        }

        if (change.BoardOrder is { } boardOrder)
        {
            ApplyOrder(connection, transaction, "boards", boardOrder.Select(id => id.ToString()).ToList());
        }
        else if (!change.DeleteBoards.IsEmpty)
        {
            CompactOrder(connection, transaction, "boards");
        }
    }

    internal static string Text(Guid id) => id.ToString("D");

    private static void InsertSnapshot(SqliteConnection c, SqliteTransaction t, SourceSnapshot snapshot)
    {
        var id = snapshot.Id.ToString();
        Exec(c, t, "INSERT INTO snapshots (id, year, season, category) VALUES ($id, $year, $season, $category)",
            ("$id", id), ("$year", snapshot.Context.Year), ("$season", (int?)snapshot.Context.Season), ("$category", (int)snapshot.Context.Category));

        foreach (var capture in snapshot.Captures)
        {
            Exec(c, t, """
                INSERT INTO season_captures (snapshot_id, year, season, category, request_url, retrieved_at, parser_version)
                VALUES ($id, $year, $season, $category, $url, $at, $parser)
                """,
                ("$id", id), ("$year", capture.Season.Year), ("$season", (int)capture.Season.Season), ("$category", (int)capture.Category),
                ("$url", capture.RequestUrl.AbsoluteUri), ("$at", capture.RetrievedAtUtc.ToString("O", CultureInfo.InvariantCulture)), ("$parser", capture.ParserVersion));

            foreach (var occurrence in capture.Occurrences)
            {
                Exec(c, t, "INSERT OR IGNORE INTO items (livechart_id) VALUES ($item)", ("$item", occurrence.Item.Value));
                var details = occurrence.Details;
                Exec(c, t, """
                    INSERT INTO occurrences (
                        snapshot_id, year, season, ordinal, livechart_id,
                        displayed_title, english_title, romanized_title, native_title,
                        rating, premiere, metadata, synopsis, studios_json, tags_json, anilist_url, myanimelist_url)
                    VALUES ($id, $year, $season, $ordinal, $item, $displayed, $english, $romanized, $native, $rating, $premiere, $metadata, $synopsis, $studios, $tags, $anilist, $mal)
                    """,
                    ("$id", id), ("$year", capture.Season.Year), ("$season", (int)capture.Season.Season), ("$ordinal", occurrence.Ordinal), ("$item", occurrence.Item.Value),
                    ("$displayed", occurrence.Titles.Displayed), ("$english", occurrence.Titles.English), ("$romanized", occurrence.Titles.Romanized), ("$native", occurrence.Titles.Native),
                    ("$rating", details.Rating), ("$premiere", details.Premiere), ("$metadata", details.Metadata), ("$synopsis", details.Synopsis),
                    ("$studios", SerializeStringArray(details.Studios)), ("$tags", SerializeStringArray(details.Tags)),
                    ("$anilist", details.AniListUrl?.AbsoluteUri), ("$mal", details.MyAnimeListUrl?.AbsoluteUri));

                for (var rank = 0; rank < occurrence.ImageCandidates.Length; rank++)
                {
                    var candidate = occurrence.ImageCandidates[rank];
                    Exec(c, t, """
                        INSERT INTO image_candidates (snapshot_id, year, season, ordinal, rank, variant, url)
                        VALUES ($id, $year, $season, $ordinal, $rank, $variant, $url)
                        """,
                        ("$id", id), ("$year", capture.Season.Year), ("$season", (int)capture.Season.Season), ("$ordinal", occurrence.Ordinal),
                        ("$rank", rank), ("$variant", candidate.Variant), ("$url", candidate.Url.AbsoluteUri));
                }
            }
        }
    }

    private static void InsertAsset(SqliteConnection c, SqliteTransaction t, AssetRecord asset)
    {
        using var existing = c.CreateCommand();
        existing.Transaction = t;
        existing.CommandText = "SELECT format, width, height, byte_length FROM assets WHERE hash = $hash";
        existing.Parameters.AddWithValue("$hash", asset.Hash.Value);
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                var same = reader.GetString(0) == AssetRecord.Extension(asset.Format) && reader.GetInt32(1) == asset.Width && reader.GetInt32(2) == asset.Height && reader.GetInt64(3) == asset.ByteLength;
                if (!same)
                {
                    throw new ProjectStoreException($"Asset {asset.Hash} already exists with different recorded properties.");
                }

                return;
            }
        }

        Exec(c, t, "INSERT INTO assets (hash, format, width, height, byte_length, relative_path) VALUES ($hash, $format, $w, $h, $len, $path)",
            ("$hash", asset.Hash.Value), ("$format", AssetRecord.Extension(asset.Format)), ("$w", asset.Width), ("$h", asset.Height), ("$len", asset.ByteLength), ("$path", asset.RelativePath));
    }

    private static void PutCollection(SqliteConnection c, SqliteTransaction t, CollectionState collection)
    {
        var id = collection.Id.ToString();
        var nextOrdinal = NextOrdinal(c, t, "collections");
        Exec(c, t, """
            INSERT INTO collections (id, name, year, season, category, applied_snapshot_id, title_language, ordinal)
            VALUES ($id, $name, $year, $season, $category, $snapshot, $language, $ordinal)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, applied_snapshot_id = excluded.applied_snapshot_id, title_language = excluded.title_language
            WHERE year = excluded.year AND season IS excluded.season AND category = excluded.category
            """,
            ("$id", id), ("$name", collection.Name), ("$year", collection.Context.Year), ("$season", (int?)collection.Context.Season),
            ("$category", (int)collection.Context.Category), ("$snapshot", collection.AppliedSnapshot.ToString()), ("$language", (int)collection.Language), ("$ordinal", nextOrdinal));

        Exec(c, t, "DELETE FROM collection_entries WHERE collection_id = $id", ("$id", id));
        for (var ordinal = 0; ordinal < collection.Entries.Length; ordinal++)
        {
            var e = collection.Entries[ordinal];
            var cover = e.Cover;
            Exec(c, t, """
                INSERT INTO collection_entries (
                    collection_id, livechart_id, ordinal, included, source_missing, title_override,
                    accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal,
                    cover_asset_hash, cover_snapshot_id, cover_year, cover_season, cover_ordinal, cover_requested_url, cover_final_url, cover_request_context)
                VALUES ($c, $item, $ordinal, $included, $missing, $override, $as, $ay, $ase, $ao, $hash, $cs, $cy, $cse, $co, $req, $final, $ctx)
                """,
                ("$c", id), ("$item", e.Item.Value), ("$ordinal", ordinal), ("$included", e.Included ? 1 : 0), ("$missing", e.SourceMissing ? 1 : 0), ("$override", e.TitleOverride),
                ("$as", e.AcceptedOccurrence.Snapshot.ToString()), ("$ay", e.AcceptedOccurrence.Season.Year), ("$ase", (int)e.AcceptedOccurrence.Season.Season), ("$ao", e.AcceptedOccurrence.Ordinal),
                ("$hash", cover?.Asset.Value), ("$cs", cover?.Occurrence.Snapshot.ToString()), ("$cy", cover?.Occurrence.Season.Year), ("$cse", (int?)cover?.Occurrence.Season.Season),
                ("$co", cover?.Occurrence.Ordinal), ("$req", cover?.RequestedUrl.AbsoluteUri), ("$final", cover?.FinalUrl.AbsoluteUri), ("$ctx", cover?.RequestContext));
        }

        using var check = c.CreateCommand();
        check.Transaction = t;
        check.CommandText = "SELECT count(*) FROM collections WHERE id = $id AND year = $year AND season IS $season AND category = $category";
        check.Parameters.AddWithValue("$id", id);
        check.Parameters.AddWithValue("$year", collection.Context.Year);
        check.Parameters.AddWithValue("$season", (object?)(int?)collection.Context.Season ?? DBNull.Value);
        check.Parameters.AddWithValue("$category", (int)collection.Context.Category);
        if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new ProjectStoreException("A collection's acquisition context cannot change.");
        }
    }

    private static void PutBoard(SqliteConnection c, SqliteTransaction t, BoardState board)
    {
        var id = board.Id.ToString();
        var nextOrdinal = NextOrdinal(c, t, "boards");
        Exec(c, t, """
            INSERT INTO boards (id, collection_id, name, ordinal) VALUES ($id, $collection, $name, $ordinal)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name WHERE collection_id = excluded.collection_id
            """,
            ("$id", id), ("$collection", board.Collection.ToString()), ("$name", board.Name), ("$ordinal", nextOrdinal));

        Exec(c, t, "DELETE FROM board_entries WHERE board_id = $id", ("$id", id));
        Exec(c, t, "DELETE FROM tiers WHERE board_id = $id", ("$id", id));
        for (var ordinal = 0; ordinal < board.Tiers.Length; ordinal++)
        {
            var tier = board.Tiers[ordinal];
            Exec(c, t, "INSERT INTO tiers (board_id, id, ordinal, name, color) VALUES ($b, $id, $ordinal, $name, $color)",
                ("$b", id), ("$id", tier.Id.ToString()), ("$ordinal", ordinal), ("$name", tier.Name), ("$color", tier.Color.ToHex()));
        }

        for (var seed = 0; seed < board.Entries.Length; seed++)
        {
            var entry = board.Entries[seed];
            var tier = board.TierOf(entry.Key);
            int? position = tier is { } placed ? board.Placements[placed].IndexOf(entry.Key) : null;
            Exec(c, t, """
                INSERT INTO board_entries (board_id, livechart_id, copy_number, seed, pinned_title, pinned_asset_hash, tier_id, tier_position)
                VALUES ($b, $item, $copy, $seed, $title, $hash, $tier, $position)
                """,
                ("$b", id), ("$item", entry.Item.Value), ("$copy", entry.CopyNumber), ("$seed", seed), ("$title", entry.PinnedTitle), ("$hash", entry.PinnedCover?.Value),
                ("$tier", tier?.ToString()), ("$position", position));
        }

        using var check = c.CreateCommand();
        check.Transaction = t;
        check.CommandText = "SELECT collection_id FROM boards WHERE id = $id";
        check.Parameters.AddWithValue("$id", id);
        if (check.ExecuteScalar() as string != board.Collection.ToString())
        {
            throw new ProjectStoreException("A board's source collection cannot change.");
        }
    }

    private static int NextOrdinal(SqliteConnection c, SqliteTransaction t, string table)
    {
        using var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = $"SELECT COALESCE(MAX(ordinal) + 1, 0) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Renumbers all rows in <paramref name="table"/> to 0..N-1 matching <paramref name="orderedIds"/> without violating UNIQUE(ordinal).</summary>
    private static void ApplyOrder(SqliteConnection c, SqliteTransaction t, string table, List<string> orderedIds)
    {
        using var countCommand = c.CreateCommand();
        countCommand.Transaction = t;
        countCommand.CommandText = $"SELECT count(*) FROM {table}";
        var existingCount = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (existingCount != orderedIds.Count)
        {
            throw new ProjectStoreException($"Cannot reorder {table}: expected {existingCount} rows but received {orderedIds.Count}.");
        }

        if (orderedIds.Count == 0)
        {
            return;
        }

        Exec(c, t, $"UPDATE {table} SET ordinal = ordinal + {RenumberOffset}");
        for (var i = 0; i < orderedIds.Count; i++)
        {
            Exec(c, t, $"UPDATE {table} SET ordinal = $ordinal WHERE id = $id", ("$ordinal", i), ("$id", orderedIds[i]));
        }
    }

    /// <summary>Compacts ordinals to 0..N-1 in existing relative order after low-level row deletions.</summary>
    private static void CompactOrder(SqliteConnection c, SqliteTransaction t, string table)
    {
        var ids = new List<string>();
        using (var command = c.CreateCommand())
        {
            command.Transaction = t;
            command.CommandText = $"SELECT id FROM {table} ORDER BY ordinal";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        ApplyOrder(c, t, table, ids);
    }

    private static void PutItemExtras(SqliteConnection c, SqliteTransaction t, AnimeExtraDetails extras)
    {
        var details = extras.Details;
        Exec(c, t, """
            INSERT INTO item_extras (
                livechart_id, retrieved_at, videos_fetched, profile_fetched, rating, premiere, metadata, synopsis, studios_json, tags_json, anilist_url, myanimelist_url)
            VALUES ($item, $at, $vf, $pf, $rating, $premiere, $metadata, $synopsis, $studios, $tags, $anilist, $mal)
            ON CONFLICT (livechart_id) DO UPDATE SET
                retrieved_at = excluded.retrieved_at,
                videos_fetched = excluded.videos_fetched,
                profile_fetched = excluded.profile_fetched,
                rating = excluded.rating,
                premiere = excluded.premiere,
                metadata = excluded.metadata,
                synopsis = excluded.synopsis,
                studios_json = excluded.studios_json,
                tags_json = excluded.tags_json,
                anilist_url = excluded.anilist_url,
                myanimelist_url = excluded.myanimelist_url
            """,
            ("$item", extras.Item.Value),
            ("$at", extras.RetrievedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
            ("$vf", extras.VideosFetched ? 1 : 0),
            ("$pf", extras.ProfileFetched ? 1 : 0),
            ("$rating", details.Rating),
            ("$premiere", details.Premiere),
            ("$metadata", details.Metadata),
            ("$synopsis", details.Synopsis),
            ("$studios", SerializeStringArray(details.Studios)),
            ("$tags", SerializeStringArray(details.Tags)),
            ("$anilist", details.AniListUrl?.AbsoluteUri),
            ("$mal", details.MyAnimeListUrl?.AbsoluteUri));

        Exec(c, t, "DELETE FROM item_videos WHERE livechart_id = $item", ("$item", extras.Item.Value));
        for (var i = 0; i < extras.Videos.Length; i++)
        {
            var video = extras.Videos[i];
            Exec(c, t, """
                INSERT INTO item_videos (livechart_id, ordinal, title, url, kind, duration, audio_language, subtitles, has_spoilers)
                VALUES ($item, $ordinal, $title, $url, $kind, $duration, $audio, $subs, $spoilers)
                """,
                ("$item", extras.Item.Value),
                ("$ordinal", i),
                ("$title", video.Title),
                ("$url", video.Url.AbsoluteUri),
                ("$kind", (int)video.Kind),
                ("$duration", video.Duration),
                ("$audio", video.AudioLanguage),
                ("$subs", video.Subtitles),
                ("$spoilers", video.HasSpoilers ? 1 : 0));
        }
    }

    private static string? SerializeStringArray(ImmutableArray<string> values) =>
        values.IsDefaultOrEmpty ? null : JsonSerializer.Serialize(values);

    private static void Exec(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object? Value)[] parameters) =>
        SqliteProjectStore.Execute(c, t, sql, parameters);
}
