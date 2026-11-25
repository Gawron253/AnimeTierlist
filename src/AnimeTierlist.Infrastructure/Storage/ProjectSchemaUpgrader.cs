using AnimeTierlist.Application.Session;
using AnimeTierlist.Application.Storage;
using Microsoft.Data.Sqlite;

namespace AnimeTierlist.Infrastructure.Storage;

/// <summary>
/// Upgrades a version-1 or version-2 project database to the current schema version (<see cref="ProjectSchema.Version"/>)
/// without losing data or mutating the original on failure. Takes a recoverable SQLite backup first, builds a fresh
/// database matching <see cref="ProjectSchema.Create"/> verbatim (so later archive exports pass exact-schema validation),
/// verifies integrity and domain invariants, and only then swaps the database file into place.
/// </summary>
internal static class ProjectSchemaUpgrader
{
    private const string BackupPrefix = ".up-bak-";
    private const string StagingPrefix = ".up-new-";

    /// <summary>
    /// Upgrades <paramref name="projectDirectory"/> from schema version 1 or 2 to <see cref="ProjectSchema.Version"/>.
    /// <paramref name="beforeInstall"/> is an optional test seam invoked after staging validation and before final file swap.
    /// </summary>
    public static void UpgradeToLatest(string projectDirectory, int fromVersion, Action? beforeInstall = null)
    {
        if (fromVersion is not (1 or 2))
        {
            throw new ProjectStoreException($"Cannot upgrade project schema from unsupported version {fromVersion}.");
        }

        var databasePath = Path.Combine(projectDirectory, SqliteProjectStore.DatabaseFileName);
        var unique = Guid.NewGuid().ToString("N")[..8];
        var backupPath = Path.Combine(projectDirectory, BackupPrefix + unique + ".db");
        var stagingPath = Path.Combine(projectDirectory, StagingPrefix + unique + ".db");

        try
        {
            // 1. Take a consistent recoverable backup of the source database (capturing any uncheckpointed WAL pages).
            BackupDatabase(databasePath, backupPath);

            // 2. Build the upgraded database into a fresh sibling file using the exact latest DDL.
            BuildLatestStaging(backupPath, stagingPath, fromVersion);

            // Optional test hook to verify failure/interruption before installation preserves the original intact.
            beforeInstall?.Invoke();

            // 3. Checkpoint and switch the source database to DELETE mode so no stale WAL/SHM sidecars remain
            //    beside the replaced database file.
            SwitchToDeleteMode(databasePath);

            // 4. Atomically replace project.sqlite with the validated upgraded database.
            SqliteConnection.ClearAllPools();
            File.Move(stagingPath, databasePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not ProjectStoreException)
        {
            throw new ProjectStoreException($"The project database could not be upgraded from schema version {fromVersion} to {ProjectSchema.Version}: {ex.Message}", ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteFile(stagingPath);
            TryDeleteFile(stagingPath + "-wal");
            TryDeleteFile(stagingPath + "-shm");
            TryDeleteFile(stagingPath + "-journal");
            TryDeleteFile(backupPath);
            TryDeleteFile(backupPath + "-wal");
            TryDeleteFile(backupPath + "-shm");
            TryDeleteFile(backupPath + "-journal");
        }
    }

    /// <summary>Copies the full committed state of <paramref name="sourcePath"/> into a new rollback-journal backup file.</summary>
    private static void BackupDatabase(string sourcePath, string backupPath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        source.Open();

        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        backup.Open();
        source.BackupDatabase(backup);
        SqliteProjectStore.Execute(backup, null, "PRAGMA journal_mode = DELETE;");
    }

    /// <summary>Creates a fresh latest-schema database, copies all data into it, validates integrity and domain rules, and closes it clean.</summary>
    private static void BuildLatestStaging(string sourceBackupPath, string stagingPath, int fromVersion)
    {
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = stagingPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        target.Open();
        SqliteProjectStore.Execute(target, null, "PRAGMA journal_mode = DELETE; PRAGMA synchronous = FULL; PRAGMA trusted_schema = OFF;");

        SqliteProjectStore.Execute(target, null, "ATTACH DATABASE $src AS src", ("$src", sourceBackupPath));
        try
        {
            using var transaction = target.BeginTransaction(deferred: false);
            SqliteProjectStore.Execute(target, transaction, ProjectSchema.Create);
            SqliteProjectStore.Execute(target, transaction, """
                INSERT INTO project_meta (key, value)
                SELECT key, value FROM src.project_meta;

                INSERT INTO items (livechart_id)
                SELECT livechart_id FROM src.items;

                INSERT INTO snapshots (id, year, season, category)
                SELECT id, year, season, category FROM src.snapshots;

                INSERT INTO season_captures (snapshot_id, year, season, category, request_url, retrieved_at, parser_version)
                SELECT snapshot_id, year, season, category, request_url, retrieved_at, parser_version FROM src.season_captures;
                """);

            if (fromVersion == 1)
            {
                SqliteProjectStore.Execute(target, transaction, """
                    INSERT INTO occurrences (
                        snapshot_id, year, season, ordinal, livechart_id,
                        displayed_title, english_title, romanized_title, native_title,
                        rating, premiere, metadata, synopsis, studios_json, tags_json, anilist_url, myanimelist_url)
                    SELECT
                        snapshot_id, year, season, ordinal, livechart_id,
                        displayed_title, english_title, romanized_title, native_title,
                        NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL
                    FROM src.occurrences;
                    """);
            }
            else
            {
                SqliteProjectStore.Execute(target, transaction, """
                    INSERT INTO occurrences (
                        snapshot_id, year, season, ordinal, livechart_id,
                        displayed_title, english_title, romanized_title, native_title,
                        rating, premiere, metadata, synopsis, studios_json, tags_json, anilist_url, myanimelist_url)
                    SELECT
                        snapshot_id, year, season, ordinal, livechart_id,
                        displayed_title, english_title, romanized_title, native_title,
                        rating, premiere, metadata, synopsis, studios_json, tags_json, NULL, NULL
                    FROM src.occurrences;
                    """);
            }

            SqliteProjectStore.Execute(target, transaction, """
                INSERT INTO image_candidates (snapshot_id, year, season, ordinal, rank, variant, url)
                SELECT snapshot_id, year, season, ordinal, rank, variant, url FROM src.image_candidates;

                INSERT INTO assets (hash, format, width, height, byte_length, relative_path)
                SELECT hash, format, width, height, byte_length, relative_path FROM src.assets;
                """);

            if (fromVersion == 1)
            {
                // Initialize collection ordinals in V1 display order (CurrentCultureIgnoreCase by name, Guid ID tie-breaker).
                var collectionOrder = ReadV1Order(target, transaction, "src.collections");
                for (var i = 0; i < collectionOrder.Count; i++)
                {
                    SqliteProjectStore.Execute(
                        target,
                        transaction,
                        """
                        INSERT INTO collections (id, name, year, season, category, applied_snapshot_id, title_language, ordinal)
                        SELECT id, name, year, season, category, applied_snapshot_id, title_language, $ordinal
                        FROM src.collections
                        WHERE id = $id;
                        """,
                        ("$id", collectionOrder[i]),
                        ("$ordinal", i));
                }
            }
            else
            {
                SqliteProjectStore.Execute(target, transaction, """
                    INSERT INTO collections (id, name, year, season, category, applied_snapshot_id, title_language, ordinal)
                    SELECT id, name, year, season, category, applied_snapshot_id, title_language, ordinal
                    FROM src.collections;
                    """);
            }

            SqliteProjectStore.Execute(target, transaction, """
                INSERT INTO collection_entries (
                    collection_id, livechart_id, ordinal, included, source_missing, title_override,
                    accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal,
                    cover_asset_hash, cover_snapshot_id, cover_year, cover_season, cover_ordinal,
                    cover_requested_url, cover_final_url, cover_request_context)
                SELECT
                    collection_id, livechart_id, ordinal, included, source_missing, title_override,
                    accepted_snapshot_id, accepted_year, accepted_season, accepted_ordinal,
                    cover_asset_hash, cover_snapshot_id, cover_year, cover_season, cover_ordinal,
                    cover_requested_url, cover_final_url, cover_request_context
                FROM src.collection_entries;
                """);

            if (fromVersion == 1)
            {
                // Initialize board ordinals in V1 display order (CurrentCultureIgnoreCase by name, Guid ID tie-breaker).
                var boardOrder = ReadV1Order(target, transaction, "src.boards");
                for (var i = 0; i < boardOrder.Count; i++)
                {
                    SqliteProjectStore.Execute(
                        target,
                        transaction,
                        """
                        INSERT INTO boards (id, collection_id, name, ordinal)
                        SELECT id, collection_id, name, $ordinal
                        FROM src.boards
                        WHERE id = $id;
                        """,
                        ("$id", boardOrder[i]),
                        ("$ordinal", i));
                }
            }
            else
            {
                SqliteProjectStore.Execute(target, transaction, """
                    INSERT INTO boards (id, collection_id, name, ordinal)
                    SELECT id, collection_id, name, ordinal
                    FROM src.boards;
                    """);
            }

            SqliteProjectStore.Execute(target, transaction, """
                INSERT INTO tiers (board_id, id, ordinal, name, color)
                SELECT board_id, id, ordinal, name, color FROM src.tiers;
                """);

            if (fromVersion == 1)
            {
                SqliteProjectStore.Execute(target, transaction, """
                    INSERT INTO board_entries (board_id, livechart_id, copy_number, seed, pinned_title, pinned_asset_hash, tier_id, tier_position)
                    SELECT board_id, livechart_id, 1, seed, pinned_title, pinned_asset_hash, tier_id, tier_position
                    FROM src.board_entries;
                    """);
            }
            else
            {
                SqliteProjectStore.Execute(target, transaction, """
                    INSERT INTO board_entries (board_id, livechart_id, copy_number, seed, pinned_title, pinned_asset_hash, tier_id, tier_position)
                    SELECT board_id, livechart_id, copy_number, seed, pinned_title, pinned_asset_hash, tier_id, tier_position
                    FROM src.board_entries;
                    """);
            }

            SqliteProjectStore.Execute(target, transaction, $"PRAGMA user_version = {ProjectSchema.Version};");
            transaction.Commit();
        }
        finally
        {
            SqliteProjectStore.Execute(target, null, "DETACH DATABASE src;");
        }

        // Verify SQLite integrity, foreign keys, and full domain invariants on the staged database before installation.
        using (var integrity = target.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (integrity.ExecuteScalar() as string != "ok")
            {
                throw new ProjectStoreException("The upgraded project database failed its integrity check.");
            }
        }

        using (var fk = target.CreateCommand())
        {
            fk.CommandText = "PRAGMA foreign_key_check;";
            using var reader = fk.ExecuteReader();
            if (reader.Read())
            {
                throw new ProjectStoreException("The upgraded project database failed its foreign key check.");
            }
        }

        _ = ProjectState.FromData(ProjectReader.Read(target));
    }

    /// <summary>Reads rows from a V1 table and orders IDs using the V1 sidebar comparer (name, then Guid ID).</summary>
    private static List<string> ReadV1Order(SqliteConnection connection, SqliteTransaction transaction, string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id, name FROM {tableName};";
        var rows = new List<(string Id, string Name)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => Guid.Parse(r.Id))
            .Select(r => r.Id)
            .ToList();
    }

    private static void SwitchToDeleteMode(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        SqliteProjectStore.Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode = DELETE;");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
